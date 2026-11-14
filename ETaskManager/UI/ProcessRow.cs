using System.ComponentModel;
using System.Runtime.CompilerServices;
using ETaskManager.Monitoring;

namespace ETaskManager.UI;

/// <summary>Per-process data, updated in place each tick. Not bound to the UI directly.</summary>
public sealed class ProcessRow
{
    public ProcessRow(ProcessSample s)
    {
        Key = s.Key;
        Pid = s.Pid;
        CreateTime = s.CreateTime;
        Name = s.Name;
        IsPseudo = s.Pid == SystemSampler.InterruptsPseudoPid;
        PidText = IsPseudo ? "—" : s.Pid.ToString();
        Update(s);
    }

    public ProcKey Key { get; }
    public uint Pid { get; }
    public long CreateTime { get; }
    public string Name { get; }
    public string PidText { get; }
    public bool IsPseudo { get; }

    public double CpuPercent { get; private set; }
    public double PrivateWsMb { get; private set; }
    public double CommitMb { get; private set; }
    public uint Threads { get; private set; }
    public uint Handles { get; private set; }
    public long CpuTime100ns { get; private set; }

    public uint ParentPid { get; private set; }

    /// <summary>"name (pid)" of the parent, "pid (exited)" if it no longer exists, or "" for none.</summary>
    public string ParentText { get; private set; } = "";

    private ProcKey? _parentKey;
    private bool _parentResolved;

    internal int Generation;

    public void ResolveParent(Dictionary<uint, ProcessRow> byPid)
    {
        if (IsPseudo || ParentPid == 0)
        {
            ParentText = "";
            return;
        }

        // The parent must have started before the child; otherwise the PID was reused
        // by an unrelated process after the real parent exited.
        ProcKey? key = byPid.TryGetValue(ParentPid, out var p) && !ReferenceEquals(p, this) && p.CreateTime <= CreateTime
            ? p.Key
            : null;
        if (_parentResolved && key == _parentKey) return; // unchanged: no string allocation

        _parentResolved = true;
        _parentKey = key;
        ParentText = key != null ? $"{p!.Name} ({ParentPid})" : $"{ParentPid} (exited)";
    }

    public void Update(ProcessSample s)
    {
        ParentPid = s.ParentPid;
        CpuPercent = Math.Round(s.CpuPercent, 1);
        PrivateWsMb = s.PrivateWorkingSet / (1024.0 * 1024.0);
        CommitMb = s.Commit / (1024.0 * 1024.0);
        Threads = s.Threads;
        Handles = s.Handles;
        CpuTime100ns = s.CpuTime100ns;
    }
}

/// <summary>
/// One visible line of the grid. The grid's collection holds a fixed set of slots; every tick
/// slot i is filled with the i-th process in sorted order. Only changed values raise
/// PropertyChanged, and only realized (on-screen) rows have bindings, so a tick is cheap and
/// the DataGrid never has to re-layout because of collection changes.
/// </summary>
public sealed class RowSlot : INotifyPropertyChanged
{
    public ProcKey Key { get; private set; }
    public bool IsPseudo { get; private set; }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _pidText = "";
    public string PidText { get => _pidText; private set => Set(ref _pidText, value); }

    private double _cpu;
    public double CpuPercent { get => _cpu; private set => Set(ref _cpu, value); }

    private double _privateWsMb;
    public double PrivateWsMb { get => _privateWsMb; private set => Set(ref _privateWsMb, value); }

    private double _commitMb;
    public double CommitMb { get => _commitMb; private set => Set(ref _commitMb, value); }

    private string _cpuTime = "";
    public string CpuTimeText { get => _cpuTime; private set => Set(ref _cpuTime, value); }

    private string _parent = "";
    public string ParentText { get => _parent; private set => Set(ref _parent, value); }

    private long _cpuTimeRaw = -1;

    public void Assign(ProcessRow r)
    {
        Key = r.Key;
        IsPseudo = r.IsPseudo;
        Name = r.Name;
        PidText = r.PidText;
        CpuPercent = r.CpuPercent;
        PrivateWsMb = Math.Round(r.PrivateWsMb, 1);
        CommitMb = Math.Round(r.CommitMb, 1);
        ParentText = r.ParentText;
        // Only reformat the CPU-time string when the underlying value changed (whole seconds).
        long secs = r.CpuTime100ns / 10_000_000;
        if (secs != _cpuTimeRaw || (r.IsPseudo && _cpuTime.Length != 0))
        {
            _cpuTimeRaw = secs;
            CpuTimeText = r.IsPseudo ? "" : FormatCpuTime(secs);
        }
    }

    private static string FormatCpuTime(long secs)
    {
        long h = secs / 3600, m = secs / 60 % 60, s = secs % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
