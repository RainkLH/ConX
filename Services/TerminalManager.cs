using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using ConX.Hubs;

namespace ConX.Services;

public class TerminalManager
{
    private readonly ConcurrentDictionary<string, List<TerminalTab>> _userTabs = new();
    private readonly ConcurrentDictionary<int, TerminalTab> _processToTab = new();
    private readonly IHubContext<TerminalHub> _hub;
    private readonly ILogger _logger;

    public TerminalManager(ILogger<TerminalManager> logger, IHubContext<TerminalHub> hub)
    {
        _logger = logger;
        _hub = hub;
    }

    public TerminalTab CreateTab(string ownerUserId, string circuitId, string type)
    {
        var sessionKey = ownerUserId + ":" + circuitId;
        _logger.LogInformation("Creating terminal tab for user {User} circuit {Circuit} type {Type}", ownerUserId, circuitId, type);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = type == "PowerShell" ? "powershell.exe" : "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "-NoLogo -NoProfile",
            // Use system default encoding to reduce garbled characters on Windows consoles
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };
        var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var tab = new TerminalTab
        {
            TabId = Guid.NewGuid().ToString(),
            OwnerUserId = ownerUserId,
            CircuitId = circuitId,
            Type = type,
            SystemProcessId = process.Id,
            Process = process,
            CreateTime = DateTime.Now,
            OutputQueue = new ConcurrentQueue<string>(),
            OutputLines = new ConcurrentQueue<string>(),
            RunningCommands = new ConcurrentDictionary<string, CommandMeta>()
        };

        _logger.LogInformation("Terminal tab {TabId} created for user {User}, pid={Pid}", tab.TabId, ownerUserId, process.Id);

        process.OutputDataReceived += async (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Detect command completion markers
                if (TryHandleCommandMarker(tab, e.Data))
                {
                    // marker processed
                }
                else
                {
                    tab.OutputQueue.Enqueue(e.Data);
                    // maintain buffered lines with max cap
                    tab.OutputLines.Enqueue(e.Data);
                    TrimOutputLines(tab, 10000); // keep max 10k lines approx
                    try { await _hub.Clients.Group(tab.CircuitId).SendAsync("TerminalOutput", tab.TabId, e.Data); } catch { }
                }
            }
        };
        process.ErrorDataReceived += async (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                tab.OutputQueue.Enqueue(e.Data);
                tab.OutputLines.Enqueue(e.Data);
                TrimOutputLines(tab, 10000);
                try { await _hub.Clients.Group(tab.CircuitId).SendAsync("TerminalOutput", tab.TabId, e.Data); } catch { }
            }
        };
        
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _userTabs.AddOrUpdate(sessionKey, new List<TerminalTab> { tab }, (k, v) => { v.Add(tab); return v; });
        _processToTab.TryAdd(process.Id, tab);
        return tab;
    }

    // Sends a command to a tab's underlying process. Returns commandId (nonce) or null on failure.
    public string? SendCommand(string ownerUserId, string circuitId, string tabId, string command, TimeSpan? timeout = null)
    {
        var tabs = GetTabsBySession(ownerUserId, circuitId);
        var tab = tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab == null)
        {
            _logger.LogWarning("SendCommand failed: tab {TabId} not found for user {User} circuit {Circuit}", tabId, ownerUserId, circuitId);
            return null;
        }
        if (tab.Process?.StandardInput == null)
        {
            _logger.LogWarning("SendCommand failed: no stdin for tab {TabId} pid {Pid}", tabId, tab.SystemProcessId);
            return null;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var meta = new CommandMeta { Id = nonce, StartTime = DateTime.Now, Output = new StringBuilder(), Tcs = new TaskCompletionSource<bool>() };
        tab.RunningCommands.TryAdd(nonce, meta);

        // Wrap command by marker depending on shell type
        string wrapped;
        if (tab.Type == "PowerShell")
        {
            // Use Write-Host to emit marker
            wrapped = $"{command}; Write-Host '__CMD_DONE__:{nonce}'";
        }
        else
        {
            // For cmd.exe, echo marker
            wrapped = $"{command} & echo __CMD_DONE__:{nonce}";
        }

        try
        {
            tab.Process.StandardInput.WriteLine(wrapped);
            tab.Process.StandardInput.Flush();
            _logger.LogInformation("Sent command to tab {TabId} pid {Pid} nonce {Nonce}", tabId, tab.SystemProcessId, nonce);
        }
        catch (Exception ex)
        {
            tab.RunningCommands.TryRemove(nonce, out _);
            _logger.LogError(ex, "SendCommand failed for tab {TabId} pid {Pid}", tabId, tab.SystemProcessId);
            return null;
        }

        // Optional: fire-and-forget timer to remove after timeout
        var to = timeout ?? TimeSpan.FromSeconds(60);
        _ = Task.Run(async () => {
            var completed = await Task.WhenAny(tab.RunningCommands[nonce].Tcs.Task, Task.Delay(to));
            if (completed != tab.RunningCommands[nonce].Tcs.Task)
            {
                // timeout
                tab.RunningCommands.TryRemove(nonce, out var _meta);
                _logger.LogWarning("Command timeout for tab {TabId} pid {Pid} nonce {Nonce}", tabId, tab.SystemProcessId, nonce);
            }
        });

        return nonce;
    }

    private bool TryHandleCommandMarker(TerminalTab tab, string data)
    {
        const string marker = "__CMD_DONE__:";
        var idx = data.IndexOf(marker);
        if (idx >= 0)
        {
            var nonce = data.Substring(idx + marker.Length).Trim();
            if (tab.RunningCommands.TryRemove(nonce, out var meta))
            {
                // mark completion
                meta.Output.AppendLine(data);
                meta.Tcs.TrySetResult(true);
                _logger.LogInformation("Command completed for tab {TabId} pid {Pid} nonce {Nonce}", tab.TabId, tab.SystemProcessId, nonce);
                // push final marker to output queue as well
                tab.OutputQueue.Enqueue(data);
                try { _hub.Clients.Group(tab.CircuitId).SendAsync("TerminalOutput", tab.TabId, data); } catch { }
                return true;
            }
        }
        return false;
    }

    private void TrimOutputLines(TerminalTab tab, int maxLines)
    {
        try
        {
            while (tab.OutputLines.Count > maxLines && tab.OutputLines.TryDequeue(out _)) { }
        }
        catch { }
    }

    // Get buffered output snapshot (last N lines)
    public List<string> GetBufferedOutput(string ownerUserId, string circuitId, string tabId, int maxLines = 1000)
    {
        var tabs = GetTabsBySession(ownerUserId, circuitId);
        var tab = tabs.FirstOrDefault(t => t.TabId == tabId);
        if (tab == null) return new List<string>();
        // copy into list
        var arr = tab.OutputLines.ToArray();
        if (arr.Length <= maxLines) return arr.ToList();
        return arr.Skip(arr.Length - maxLines).ToList();
    }

    public List<TerminalTab> GetTabsBySession(string ownerUserId, string circuitId)
    {
        var sessionKey = ownerUserId + ":" + circuitId;
        if (_userTabs.TryGetValue(sessionKey, out var list)) return list;
        return new List<TerminalTab>();
    }

    public TerminalTab? GetByProcessId(int pid)
    {
        _processToTab.TryGetValue(pid, out var tab);
        return tab;
    }

    public bool RemoveTab(string ownerUserId, string circuitId, string tabId, bool terminateProcess)
    {
        var sessionKey = ownerUserId + ":" + circuitId;
        if (!_userTabs.TryGetValue(sessionKey, out var list)) return false;
        var tab = list.FirstOrDefault(t => t.TabId == tabId);
        if (tab == null) return false;
        list.Remove(tab);
        _processToTab.TryRemove(tab.SystemProcessId, out _);
        if (terminateProcess)
        {
            try { tab.Process?.Kill(true); _logger.LogInformation("Terminated process {Pid} when removing tab {TabId}", tab.SystemProcessId, tab.TabId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to terminate process {Pid} for tab {TabId}", tab.SystemProcessId, tab.TabId); }
        }
        _logger.LogInformation("Removed tab {TabId} for user {User} circuit {Circuit}", tabId, ownerUserId, circuitId);
        return true;
    }

    public List<string> PollOutputs(string ownerUserId, string circuitId, string tabId)
    {
        var tabs = GetTabsBySession(ownerUserId, circuitId);
        var tab = tabs.FirstOrDefault(t => t.TabId == tabId);
        var outputs = new List<string>();
        if (tab == null) return outputs;
        while (tab.OutputQueue.TryDequeue(out var line))
        {
            outputs.Add(line);
        }
        return outputs;
    }
}

public class TerminalTab
{
    public string TabId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string CircuitId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int SystemProcessId { get; set; }
    public DateTime CreateTime { get; set; }
    public Process? Process { get; set; }
    public ConcurrentQueue<string> OutputQueue { get; set; } = new();
    public ConcurrentQueue<string> OutputLines { get; set; } = new();
    public ConcurrentDictionary<string, CommandMeta> RunningCommands { get; set; } = new();
}

public class CommandMeta
{
    public string Id { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public StringBuilder Output { get; set; } = new StringBuilder();
    public TaskCompletionSource<bool> Tcs { get; set; } = new TaskCompletionSource<bool>();
}
