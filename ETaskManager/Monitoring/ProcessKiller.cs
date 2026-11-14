using System.Runtime.InteropServices;
using System.Security.Principal;
using ETaskManager.Native;
using static ETaskManager.Native.NativeMethods;

namespace ETaskManager.Monitoring;

public enum KillStatus
{
    Success,
    AlreadyExited,
    PidReused,
    AccessDenied,
    Critical,
    Failed,
}

public sealed record KillTarget(uint Pid, long CreateTime, string Name);

public sealed record KillResult(KillTarget Target, KillStatus Status, int Win32Error = 0);

public static class ProcessKiller
{
    // Killing any of these crashes / logs off Windows.
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Secure System", "Registry", "Memory Compression",
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe",
        "LsaIso.exe",
    };

    public static bool IsElevated { get; } = ComputeIsElevated();

    private static bool ComputeIsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Returns a reason string if the process must not be killed, otherwise null.</summary>
    public static string? GetBlockReason(uint pid, string name)
    {
        if (pid == SystemSampler.InterruptsPseudoPid) return "This row represents hardware interrupt / DPC time, not a process.";
        if (pid <= 4) return $"\"{name}\" is a core part of Windows and cannot be terminated.";
        if (pid == (uint)Environment.ProcessId) return "That is ETaskManager itself. Just close the window.";
        if (CriticalNames.Contains(name)) return $"\"{name}\" is a critical Windows process. Terminating it would crash or log off Windows.";
        return null;
    }

    /// <summary>All descendants of <paramref name="root"/>, deepest first.</summary>
    public static List<KillTarget> FindDescendants(IReadOnlyList<ProcessSample> processes, KillTarget root)
    {
        var byParent = processes
            .Where(p => p.Pid != SystemSampler.InterruptsPseudoPid)
            .GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<KillTarget>();
        var visited = new HashSet<uint> { root.Pid };

        void Walk(uint pid, long parentCreate, int depth)
        {
            if (depth > 64 || !byParent.TryGetValue(pid, out var kids)) return;
            foreach (var k in kids)
            {
                // Child must have been created after the parent, otherwise ParentPid refers
                // to an older process that happened to have the same PID.
                if (k.CreateTime < parentCreate || !visited.Add(k.Pid)) continue;
                Walk(k.Pid, k.CreateTime, depth + 1);
                result.Add(new KillTarget(k.Pid, k.CreateTime, k.Name));
            }
        }

        Walk(root.Pid, root.CreateTime, 0);
        return result;
    }

    /// <summary>Terminates the process after verifying it is still the same instance.</summary>
    public static KillResult Kill(KillTarget t)
    {
        if (GetBlockReason(t.Pid, t.Name) != null) return new KillResult(t, KillStatus.Critical);

        using var h = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, t.Pid);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return err switch
            {
                ERROR_INVALID_PARAMETER => new KillResult(t, KillStatus.AlreadyExited, err),
                ERROR_ACCESS_DENIED => new KillResult(t, KillStatus.AccessDenied, err),
                _ => new KillResult(t, KillStatus.Failed, err),
            };
        }

        // Fail closed: if we can't prove it is the same process instance, don't kill it.
        if (!GetProcessTimes(h, out long create, out _, out _, out _))
            return new KillResult(t, KillStatus.Failed, Marshal.GetLastWin32Error());
        if (create != t.CreateTime)
            return new KillResult(t, KillStatus.PidReused);

        try
        {
            if (IsProcessCritical(h, out bool critical) && critical)
                return new KillResult(t, KillStatus.Critical);
        }
        catch (EntryPointNotFoundException) { /* pre-8.1, ignore */ }

        if (!TerminateProcess(h, 1))
        {
            int err = Marshal.GetLastWin32Error();
            return new KillResult(t, err == ERROR_ACCESS_DENIED ? KillStatus.AccessDenied : KillStatus.Failed, err);
        }

        WaitForSingleObject(h, 3000);
        return new KillResult(t, KillStatus.Success);
    }

    public static string Describe(KillResult r) => r.Status switch
    {
        KillStatus.Success => $"Terminated {r.Target.Name} (PID {r.Target.Pid}).",
        KillStatus.AlreadyExited => $"{r.Target.Name} (PID {r.Target.Pid}) has already exited.",
        KillStatus.PidReused => $"PID {r.Target.Pid} now belongs to a different process; nothing was killed.",
        KillStatus.AccessDenied => IsElevated
            ? $"Windows denied access to {r.Target.Name} (PID {r.Target.Pid}). It is probably a protected process (anti-malware or system service)."
            : $"Access denied to {r.Target.Name} (PID {r.Target.Pid}). It runs with higher privileges; restart ETaskManager as administrator.",
        KillStatus.Critical => $"{r.Target.Name} (PID {r.Target.Pid}) is a critical system process and was not terminated.",
        _ => $"Could not terminate {r.Target.Name} (PID {r.Target.Pid}): {new System.ComponentModel.Win32Exception(r.Win32Error).Message}",
    };
}
