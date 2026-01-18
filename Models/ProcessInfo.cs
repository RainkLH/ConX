using System;

namespace ConX.Models;

public class ProcessInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int ThreadCount { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public string CommandLine { get; set; } = string.Empty;
    public DateTime? StartTime { get; set; }
    public string Status { get; set; } = string.Empty;
    // 是否与 TerminalTab 关联
    public bool IsAssociated { get; set; }
    public string? AssociatedTabId { get; set; }
}
