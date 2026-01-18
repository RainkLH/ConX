using System.Diagnostics;
using System.Management;
using ConX.Models;

namespace ConX.Services;

public class ProcessService
{
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly TerminalManager _terminalManager;
    private readonly AuditService _audit;
    private readonly object _lockObj = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ILogger<ProcessService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _refreshLoop;
    private TimeSpan _refreshInterval = TimeSpan.FromSeconds(8);
    private int _refreshSampleMs = 200;
    private bool _initialized;

    private List<ProcessInfo> _processInfos = new();
    public List<ProcessInfo> ProcessInfos
    {
        get { lock (_lockObj) { return _processInfos.ToList(); } }
        private set { lock (_lockObj) { _processInfos = value; } }
    }

    // simple protected process name list (should be configurable)
    private readonly HashSet<string> _protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss","wininit","winlogon","lsass","services","svchost",
    };

    public ProcessService(ILogger<ProcessService> logger, TerminalManager terminalManager, AuditService audit)
    {
        _logger = logger;
        _terminalManager = terminalManager;
        _audit = audit;
    }

    public async Task Init(TimeSpan? refreshInterval = null, int sampleMs = 200)
    {
        if (_initialized) return;
        _refreshInterval = refreshInterval ?? _refreshInterval;
        _refreshSampleMs = sampleMs;
        _cts = new CancellationTokenSource();

        _logger.LogInformation("ProcessService initializing: interval={Interval}s sampleMs={SampleMs}", _refreshInterval.TotalSeconds, _refreshSampleMs);
        await ForceRefreshAsync();

        _refreshLoop = Task.Run(() => RefreshLoopAsync(_cts.Token));
        _initialized = true;
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        _logger.LogInformation("ProcessService refresh loop started");
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ForceRefreshAsync();
                await Task.Delay(_refreshInterval, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try { _audit.Log($"ProcessService refresh loop error: {ex}"); } catch { }
                _logger.LogError(ex, "ProcessService refresh loop error");
            }
        }
        _logger.LogInformation("ProcessService refresh loop stopping");
    }

    public async Task ForceRefreshAsync(string? filter = null)
    {
        await _refreshLock.WaitAsync();
        try
        {
            var list = await GetProcessesAsync(_refreshSampleMs, filter);
            ProcessInfos = list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ForceRefreshAsync failed");
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // Get processes with short CPU sampling
    private async Task<List<ProcessInfo>> GetProcessesAsync(int sampleMs = 200, string? filter = null)
    {
        try
        {
            var procs = Process.GetProcesses();
            var dict0 = new Dictionary<int, TimeSpan>();
            foreach (var p in procs)
            {
                try { dict0[p.Id] = p.TotalProcessorTime; }
                catch { /* access denied or exited, ignore */ }
            }

            await Task.Delay(sampleMs);

            // CommandLine via WMI (Windows only)
            var processCommandLine = GetProcessCommandLine();

            var list = new List<ProcessInfo>();
            foreach (var p in procs)
            {
                var info = new ProcessInfo
                {
                    ProcessId = p.Id,
                    ProcessName = GetProcessDisplayName(p),
                    ThreadCount = SafeGetThreadCount(p),
                    MemoryBytes = SafeGetWorkingSet(p),
                    StartTime = TryGetStartTime(p),
                };

                // lightweight filter before expensive work
                if (!string.IsNullOrEmpty(filter))
                {
                    var nameMatches = info.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    var cmd = processCommandLine.TryGetValue(p.Id, out var cl) ? cl : "-";
                    var cmdMatches = cmd.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    if (!nameMatches && !cmdMatches) continue;
                }

                try { info.Status = p.HasExited ? "Exited" : "Running"; } catch { info.Status = "-"; }

                if (processCommandLine.TryGetValue(p.Id, out var commandLine))
                    info.CommandLine = commandLine;
                else
                    info.CommandLine = "-";

                try
                {
                    if (dict0.TryGetValue(p.Id, out var t0))
                    {
                        var t1 = p.TotalProcessorTime;
                        var delta = (t1 - t0).TotalMilliseconds;
                        var cpu = delta / (sampleMs * _processorCount) * 100.0;
                        info.CpuPercent = Math.Round(cpu, 2);
                    }
                    else
                    {
                        info.CpuPercent = -1;
                    }
                }
                catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 5)
                {
                    info.CpuPercent = -1; // access denied
                }
                catch { info.CpuPercent = -1; }

                list.Add(info);
            }

            var ordered = list.OrderByDescending(x => x.CpuPercent).ToList();
            return ordered;
        }
        catch (Exception ex)
        {
            try { _audit.Log($"ProcessService.GetProcessesAsync failed: {ex}"); } catch { }
            _logger.LogError(ex, "GetProcessesAsync failed");
            return new List<ProcessInfo>();
        }
    }

    private int SafeGetThreadCount(Process p)
    {
        try { return p.Threads?.Count ?? 0; } catch { return 0; }
    }
    private long SafeGetWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private string GetProcessDisplayName(Process p)
    {
        try
        {
            if (!string.IsNullOrEmpty(p.MainModule?.FileName))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(p.MainModule.FileName);
                if (!string.IsNullOrEmpty(versionInfo.FileDescription))
                    return versionInfo.FileDescription;
            }
        }
        catch { }
        return p.ProcessName;
    }

    private DateTime? TryGetStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    private Dictionary<int, string> GetProcessCommandLine()
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                return result;

            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                try
                {
                    var pid = Convert.ToInt32(mo["ProcessId"]);
                    var cmdLine = (mo["CommandLine"] ?? "-")?.ToString() ?? "-";
                    result[pid] = cmdLine;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            try { _audit.Log($"GetProcessCommandLine failed: {ex}"); } catch { }
            _logger.LogWarning(ex, "GetProcessCommandLine failed");
        }
        return result;
    }

    public List<ProcessInfo> QueryProcesses(string? filter)
    {
        var snapshot = ProcessInfos;
        if (!string.IsNullOrEmpty(filter))
        {
            snapshot = snapshot
                .Where(info => info.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) || info.CommandLine.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return snapshot.ToList();
    }

    public async Task<(bool ok, string message)> TryKillProcessAsync(int pid, string requestedBy)
    {
        await _refreshLock.WaitAsync();
        try
        {
            var p = Process.GetProcessById(pid);
            if (p == null) return (false, "Process not found");

            if (_protected.Contains(p.ProcessName))
                return (false, "Refused: protected system process");

            var tab = _terminalManager.GetByProcessId(pid);
            if (tab != null && tab.RunningCommands != null && tab.RunningCommands.Count > 0)
                return (false, "Process is associated with an active terminal and has running commands");

            try
            {
                p.CloseMainWindow();
                var exited = p.WaitForExit(2000);
                if (!exited)
                {
                    p.Kill(true);
                    p.WaitForExit(2000);
                }
            }
            catch
            {
                try { p.Kill(true); } catch { }
            }

            _audit.Log($"Process {pid} ({p.ProcessName}) terminated by {requestedBy}");
            return (true, "Terminated");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _refreshLock.Release();
            try { await ForceRefreshAsync(); } catch (Exception ex) { try { _audit.Log($"Force refresh after kill failed: {ex}"); } catch { } }
        }
    }
}
