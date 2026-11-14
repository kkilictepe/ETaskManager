using System.Diagnostics;
using System.Runtime.InteropServices;
using ETaskManager.Native;
using static ETaskManager.Native.NativeMethods;

namespace ETaskManager.Monitoring;

/// <summary>
/// Samples per-CPU usage, memory and every process on a dedicated, highest-priority thread.
///
/// CPU math (time based, so the numbers always add up to the total):
///   per core   busy% = 1 - dIdle / d(Kernel + User)          (Kernel already includes Idle)
///   per process  %   = d(Kernel + User of process) / sum over cores of d(Kernel + User)
/// The denominator is the real elapsed CPU time measured by the kernel, NOT a nominal
/// interval, so late ticks under 100% load don't inflate the values.
///
/// Note: SystemProcessorPerformanceInformation reports the current processor group only,
/// which covers machines with up to 64 logical processors.
/// </summary>
public sealed class SystemSampler : IDisposable
{
    public const uint InterruptsPseudoPid = uint.MaxValue;

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private volatile int _intervalMs = 1000;

    private IntPtr _procBuf;
    private int _procBufSize = 1024 * 1024;
    private IntPtr _cpuBuf;
    private int _cpuBufSize;

    private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[]? _prevCpu;
    private Dictionary<ProcKey, (long cpuTime, string name)> _prevProc = new();
    private bool _hasPrev;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _prevTicks;

    /// <summary>Raised on the sampler thread. Handlers must be fast and must not block.</summary>
    public event Action<Snapshot>? SnapshotReady;
    public event Action<Exception>? SampleFailed;

    public SystemSampler()
    {
        _procBuf = Marshal.AllocHGlobal(_procBufSize);
        _cpuBufSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>() * 256;
        _cpuBuf = Marshal.AllocHGlobal(_cpuBufSize);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ETaskManager sampler",
            Priority = ThreadPriority.Highest,
        };
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = Math.Clamp(value, 250, 10_000); _wake.Set(); }
    }

    public void Start() => _thread.Start();

    /// <summary>Wall-clock duration of the last sample, for diagnostics.</summary>
    public double LastSampleCostMs { get; private set; }

    private void Run()
    {
        // Prime the baseline so the first published snapshot already has real deltas.
        try { TakeSample(); } catch { /* ignored, next tick retries */ }
        _wake.WaitOne(500);

        while (!_stop)
        {
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                var snap = TakeSample();
                LastSampleCostMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (snap != null) SnapshotReady?.Invoke(snap);
            }
            catch (Exception ex)
            {
                SampleFailed?.Invoke(ex);
            }
            _wake.WaitOne(_intervalMs);
        }
    }

    private Snapshot? TakeSample()
    {
        // Take both kernel snapshots back to back so they describe the same instant.
        var cpu = ReadProcessorTimes();
        int procBytes = ReadProcessBuffer();
        long nowTicks = _clock.ElapsedTicks;
        double elapsedSec = _hasPrev ? (nowTicks - _prevTicks) / (double)Stopwatch.Frequency : 0;
        _prevTicks = nowTicks;

        int n = cpu.Length;
        var corePct = new double[n];
        long sumTotal = 0, sumIdle = 0, sumIntrDpc = 0;

        if (_prevCpu != null && _prevCpu.Length == n)
        {
            for (int i = 0; i < n; i++)
            {
                long dIdle = cpu[i].IdleTime - _prevCpu[i].IdleTime;
                long dTotal = (cpu[i].KernelTime - _prevCpu[i].KernelTime) + (cpu[i].UserTime - _prevCpu[i].UserTime);
                long dIntr = (cpu[i].DpcTime - _prevCpu[i].DpcTime) + (cpu[i].InterruptTime - _prevCpu[i].InterruptTime);
                corePct[i] = dTotal > 0 ? Clamp100(100.0 * (1.0 - (double)dIdle / dTotal)) : 0;
                sumTotal += dTotal;
                sumIdle += dIdle;
                sumIntrDpc += dIntr;
            }
        }
        _prevCpu = cpu;

        double totalPct = sumTotal > 0 ? Clamp100(100.0 * (1.0 - (double)sumIdle / sumTotal)) : 0;
        double intrPct = sumTotal > 0 ? Clamp100(100.0 * sumIntrDpc / sumTotal) : 0;

        // Denominator for per-process %: total CPU time that elapsed across all cores.
        double denom = sumTotal;
        if (denom <= 0 && elapsedSec > 0) denom = elapsedSec * 10_000_000.0 * n;

        var processes = ParseProcesses(procBytes, denom, out long threadSum, out long handleSum);

        bool publish = _hasPrev;
        _hasPrev = true;
        if (!publish) return null;

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        ulong commitTotal = 0, commitLimit = 0, cached = 0;
        if (GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        {
            ulong page = perf.PageSize;
            commitTotal = perf.CommitTotal * page;
            commitLimit = perf.CommitLimit * page;
            cached = perf.SystemCache * page;
        }

        // Pseudo process for interrupt / DPC time, which is not charged to any process.
        processes.Add(new ProcessSample(InterruptsPseudoPid, 0, 0, "System interrupts (DPC / ISR)",
            intrPct, 0, 0, 0, 0, 0, 0));

        var sys = new SystemSample(corePct, totalPct, intrPct, mem.ullTotalPhys, mem.ullAvailPhys,
            commitTotal, commitLimit, cached, processes.Count - 1, threadSum, handleSum);

        return new Snapshot(sys, processes, elapsedSec);
    }

    private static double Clamp100(double v) => v < 0 ? 0 : v > 100 ? 100 : v;

    private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] ReadProcessorTimes()
    {
        int structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, _cpuBuf, _cpuBufSize, out int ret);
        if (status != STATUS_SUCCESS)
            throw new InvalidOperationException($"NtQuerySystemInformation(SPPI) failed: 0x{status:X8}");

        int count = ret / structSize;
        var result = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[count];
        for (int i = 0; i < count; i++)
            result[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(_cpuBuf + i * structSize);
        return result;
    }

    private int ReadProcessBuffer()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int status = NtQuerySystemInformation(SystemProcessInformation, _procBuf, _procBufSize, out int needed);
            if (status == STATUS_SUCCESS) return needed;
            if (status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL)
            {
                // Processes can appear between calls, so leave some headroom.
                int newSize = Math.Max(needed, _procBufSize) + 256 * 1024;
                Marshal.FreeHGlobal(_procBuf);
                _procBuf = Marshal.AllocHGlobal(newSize);
                _procBufSize = newSize;
                continue;
            }
            throw new InvalidOperationException($"NtQuerySystemInformation(SPI) failed: 0x{status:X8}");
        }
        throw new InvalidOperationException("Process list kept growing; giving up this tick.");
    }

    private unsafe List<ProcessSample> ParseProcesses(int bytes, double denom, out long threadSum, out long handleSum)
    {
        var list = new List<ProcessSample>(Math.Max(64, _prevProc.Count + 16));
        var next = new Dictionary<ProcKey, (long, string)>(_prevProc.Count + 16);
        threadSum = 0;
        handleSum = 0;

        byte* basePtr = (byte*)_procBuf;
        byte* end = basePtr + bytes;
        byte* p = basePtr;

        while (p + 0x100 <= end)
        {
            uint nextOffset = *(uint*)(p + Spi.NextEntryOffset);
            uint threads = *(uint*)(p + Spi.NumberOfThreads);
            long privWs = *(long*)(p + Spi.WorkingSetPrivateSize);
            long create = *(long*)(p + Spi.CreateTime);
            long user = *(long*)(p + Spi.UserTime);
            long kernel = *(long*)(p + Spi.KernelTime);
            uint pid = (uint)(ulong)*(nint*)(p + Spi.UniqueProcessId);
            uint ppid = (uint)(ulong)*(nint*)(p + Spi.InheritedFromUniqueProcessId);
            uint handles = *(uint*)(p + Spi.HandleCount);
            uint session = *(uint*)(p + Spi.SessionId);
            long commit = (long)*(nuint*)(p + Spi.PrivatePageCount);

            long cpuTime = user + kernel;
            var key = new ProcKey(pid, create);

            string name;
            long delta;
            if (_prevProc.TryGetValue(key, out var prev))
            {
                name = prev.name;
                delta = cpuTime - prev.cpuTime;
            }
            else
            {
                name = ReadName(p, pid);
                // Not present in the previous snapshot => it started during this interval,
                // so all of its CPU time was spent within the interval.
                delta = _hasPrev ? cpuTime : 0;
            }
            next[key] = (cpuTime, name);

            if (pid != 0) // System Idle Process is not a real process; skip it from the list.
            {
                double pct = denom > 0 ? Clamp100(100.0 * Math.Max(0, delta) / denom) : 0;
                list.Add(new ProcessSample(pid, ppid, create, name, pct, cpuTime, privWs, commit, threads, handles, session));
                threadSum += threads;
                handleSum += handles;
            }

            if (nextOffset == 0) break;
            p += nextOffset;
        }

        _prevProc = next;
        return list;
    }

    private static unsafe string ReadName(byte* entry, uint pid)
    {
        ushort lenBytes = *(ushort*)(entry + Spi.ImageNameLength);
        IntPtr buf = *(IntPtr*)(entry + Spi.ImageNameBuffer);
        if (lenBytes == 0 || buf == IntPtr.Zero)
            return pid == 0 ? "System Idle Process" : pid == 4 ? "System" : $"<pid {pid}>";
        return new string((char*)buf, 0, lenBytes / 2);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(2000)) return; // still running: leak buffers rather than crash
        if (_procBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_procBuf); _procBuf = IntPtr.Zero; }
        if (_cpuBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_cpuBuf); _cpuBuf = IntPtr.Zero; }
        _wake.Dispose();
    }
}
