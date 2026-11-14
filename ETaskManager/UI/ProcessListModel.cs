using System.Collections.ObjectModel;
using ETaskManager.Monitoring;

namespace ETaskManager.UI;

public enum SortKey { Name, Pid, Cpu, PrivateWs, Commit, Threads, Handles, CpuTime, Parent }

/// <summary>
/// Keeps one row object per process instance and updates it in place each tick.
///
/// Sorting and filtering are done here, not by a CollectionView: a CollectionView Refresh()
/// raises a Reset, which makes the DataGrid re-layout everything (≈0.5 s per tick with
/// ~800 processes), and even per-item Insert/Remove costs ~150 ms. Instead the grid shows a
/// stable list of <see cref="RowSlot"/>s whose values are reassigned in sorted order.
/// </summary>
public sealed class ProcessListModel
{
    private readonly Dictionary<ProcKey, ProcessRow> _map = new();
    private readonly RowComparer _comparer = new();
    private int _generation;
    private string _filter = "";

    public ObservableCollection<RowSlot> Visible { get; } = new();
    public IReadOnlyList<ProcessSample> LastProcesses { get; private set; } = Array.Empty<ProcessSample>();
    public int Count => _map.Count;

    public SortKey SortKey => _comparer.Key;
    public bool Descending => _comparer.Descending;

    public void SetSort(SortKey key, bool descending)
    {
        _comparer.Key = key;
        _comparer.Descending = descending;
        Resync();
    }

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value?.Trim() ?? "";
            Resync();
        }
    }

    public ProcessRow? Find(ProcKey key) => _map.TryGetValue(key, out var r) ? r : null;

    public void Update(IReadOnlyList<ProcessSample> processes)
    {
        LastProcesses = processes;
        int gen = ++_generation;

        foreach (var s in processes)
        {
            if (_map.TryGetValue(s.Key, out var row))
            {
                row.Update(s);
            }
            else
            {
                row = new ProcessRow(s);
                _map[s.Key] = row;
            }
            row.Generation = gen;
        }

        if (_map.Count != processes.Count)
        {
            var dead = new List<ProcKey>();
            foreach (var (k, r) in _map)
                if (r.Generation != gen) dead.Add(k);
            foreach (var k in dead) _map.Remove(k);
        }

        // Resolve parent names. A PID is unique among live processes at one instant.
        var byPid = new Dictionary<uint, ProcessRow>(_map.Count);
        foreach (var r in _map.Values) byPid[r.Pid] = r;
        foreach (var r in _map.Values) r.ResolveParent(byPid);

        Resync();
    }

    private bool Matches(ProcessRow r) =>
        _filter.Length == 0
        || r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || r.PidText.StartsWith(_filter, StringComparison.Ordinal);

    /// <summary>Index of the slot currently showing <paramref name="key"/>, or -1.</summary>
    public int IndexOf(ProcKey key) => _slotIndex.TryGetValue(key, out int i) ? i : -1;

    private readonly Dictionary<ProcKey, int> _slotIndex = new();

    /// <summary>Fills the slots with the filtered + sorted rows. Only the slot count ever changes.</summary>
    private void Resync()
    {
        var desired = new List<ProcessRow>(_map.Count);
        foreach (var r in _map.Values)
            if (Matches(r)) desired.Add(r);
        desired.Sort(_comparer);

        while (Visible.Count > desired.Count) Visible.RemoveAt(Visible.Count - 1);
        while (Visible.Count < desired.Count) Visible.Add(new RowSlot());

        _slotIndex.Clear();
        for (int i = 0; i < desired.Count; i++)
        {
            Visible[i].Assign(desired[i]);
            _slotIndex[desired[i].Key] = i;
        }
    }

    private sealed class RowComparer : IComparer<ProcessRow>
    {
        public SortKey Key = SortKey.Cpu;
        public bool Descending = true;

        public int Compare(ProcessRow? a, ProcessRow? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            int c = Key switch
            {
                SortKey.Name => string.Compare(a!.Name, b!.Name, StringComparison.OrdinalIgnoreCase),
                SortKey.Pid => a!.Pid.CompareTo(b!.Pid),
                SortKey.Cpu => a!.CpuPercent.CompareTo(b!.CpuPercent),
                SortKey.PrivateWs => a!.PrivateWsMb.CompareTo(b!.PrivateWsMb),
                SortKey.Commit => a!.CommitMb.CompareTo(b!.CommitMb),
                SortKey.Threads => a!.Threads.CompareTo(b!.Threads),
                SortKey.Handles => a!.Handles.CompareTo(b!.Handles),
                SortKey.CpuTime => a!.CpuTime100ns.CompareTo(b!.CpuTime100ns),
                SortKey.Parent => string.Compare(a!.ParentText, b!.ParentText, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            if (Descending) c = -c;
            if (c != 0) return c;

            // Deterministic tie-breakers so equal rows don't jitter between ticks.
            c = string.Compare(a!.Name, b!.Name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b!.Pid);
            return c != 0 ? c : a.CreateTime.CompareTo(b.CreateTime);
        }
    }
}
