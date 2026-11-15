namespace ETaskManager.Monitoring;

/// <summary>
/// Thread-safe ring buffer of recent snapshots, fed directly from the sampler thread.
/// Independent of the UI, so readers (the MCP server) keep working while the list is paused or the UI is busy.
/// </summary>
public sealed class SnapshotHistory
{
    public readonly record struct Entry(DateTime TimeUtc, Snapshot Snapshot);

    private readonly Entry[] _buf;
    private readonly object _lock = new();
    private int _next;
    private int _count;

    public SnapshotHistory(int capacity = 300) => _buf = new Entry[capacity];

    /// <summary>Called on the sampler thread; O(1), never blocks for long.</summary>
    public void Add(Snapshot snap)
    {
        lock (_lock)
        {
            _buf[_next] = new Entry(DateTime.UtcNow, snap);
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    public Entry? Latest
    {
        get
        {
            lock (_lock)
                return _count == 0 ? null : _buf[(_next - 1 + _buf.Length) % _buf.Length];
        }
    }

    /// <summary>
    /// Newest-first entries covering roughly the last <paramref name="seconds"/> of sampling time.
    /// Always returns at least the latest entry when there is one.
    /// </summary>
    public List<Entry> Window(double seconds)
    {
        var result = new List<Entry>();
        lock (_lock)
        {
            double covered = 0;
            for (int i = 0; i < _count; i++)
            {
                var e = _buf[(_next - 1 - i + 2 * _buf.Length) % _buf.Length];
                result.Add(e);
                covered += e.Snapshot.IntervalSeconds;
                if (covered >= seconds - 0.05) break;
            }
        }
        return result;
    }
}
