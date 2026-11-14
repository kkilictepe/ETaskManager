namespace ETaskManager.Monitoring;

/// <summary>Identity of a process instance. PIDs get reused; PID + CreateTime does not.</summary>
public readonly record struct ProcKey(uint Pid, long CreateTime);

public sealed record ProcessSample(
    uint Pid,
    uint ParentPid,
    long CreateTime,
    string Name,
    double CpuPercent,
    long CpuTime100ns,
    long PrivateWorkingSet,
    long Commit,
    uint Threads,
    uint Handles,
    uint SessionId)
{
    public ProcKey Key => new(Pid, CreateTime);
}

public sealed record SystemSample(
    double[] CorePercents,
    double TotalPercent,
    double InterruptDpcPercent,
    ulong TotalPhys,
    ulong AvailPhys,
    ulong CommitTotal,
    ulong CommitLimit,
    ulong Cached,
    int ProcessCount,
    long ThreadCount,
    long HandleCount);

public sealed record Snapshot(SystemSample System, IReadOnlyList<ProcessSample> Processes, double IntervalSeconds);
