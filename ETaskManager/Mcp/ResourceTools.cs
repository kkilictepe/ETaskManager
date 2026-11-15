using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ETaskManager.Monitoring;
using static ETaskManager.Native.NativeMethods;

namespace ETaskManager.Mcp;

/// <summary>Read-only MCP tools answering "what is eating my CPU / memory?" from the sampler's history.</summary>
public sealed class ResourceTools
{
    private const string CpuNote =
        "cpu_percent is the share of the WHOLE machine (all logical processors together = 100%). " +
        "One fully busy thread on a machine with N logical processors shows as 100/N %.";

    private readonly SnapshotHistory _history;

    public ResourceTools(SnapshotHistory history) => _history = history;

    public sealed class ToolException(string message) : Exception(message);

    // ------------------------------------------------------------------ definitions

    public JsonArray ListTools() =>
    [
        Tool("get_system_overview",
            "Current CPU and memory usage of this Windows machine: total and per-logical-processor CPU, " +
            "interrupt/DPC time, physical memory, commit charge, process/thread/handle counts, and the top " +
            "processes by CPU and by memory. Start here when asked what is slowing the machine down. " + CpuNote,
            new JsonObject
            {
                ["window_seconds"] = Num("Average CPU over this many seconds of recent history (default 10, max 300). 0 = latest sample only."),
                ["top"] = Num("How many top processes to include per list (default 5, max 25)."),
            }),
        Tool("get_top_processes",
            "Processes using the most CPU or memory, with CPU averaged over a recent window so short spikes don't " +
            "mislead. Can group processes with the same image name (e.g. 40x chrome.exe) and filter by name or PID. " + CpuNote +
            " Memory is the private working set (RAM used only by that process); commit is private bytes including paged-out memory.",
            new JsonObject
            {
                ["sort_by"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("cpu", "memory", "commit"), ["description"] = "Sort key (default cpu)." },
                ["limit"] = Num("Maximum number of rows (default 15, max 200)."),
                ["window_seconds"] = Num("Average CPU over this many seconds (default 10, max 300). 0 = latest sample only."),
                ["group_by_name"] = new JsonObject { ["type"] = "boolean", ["description"] = "Sum processes that share an image name (default false)." },
                ["name_filter"] = new JsonObject { ["type"] = "string", ["description"] = "Case-insensitive substring of the process name, or an exact PID." },
            }),
        Tool("get_process_details",
            "Everything known about one process: CPU (latest, average, peak over the window), memory, threads, " +
            "handles, start time, executable path, command line, parent chain and direct children, plus totals for " +
            "the whole process tree. Use it to identify which program a PID belongs to (e.g. which node.exe). " + CpuNote,
            new JsonObject
            {
                ["pid"] = Num("Process ID."),
                ["window_seconds"] = Num("Window for the CPU average/peak (default 10, max 300)."),
            },
            required: ["pid"]),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required != null) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
        };
    }

    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    public bool HasTool(string name) => name is "get_system_overview" or "get_top_processes" or "get_process_details";

    // ------------------------------------------------------------------ dispatch

    /// <summary>Runs a tool and returns its JSON text. Throws <see cref="ToolException"/> for bad input / no data.</summary>
    public string Call(string name, JsonElement args)
    {
        var result = name switch
        {
            "get_system_overview" => SystemOverview(args),
            "get_top_processes" => TopProcesses(args),
            "get_process_details" => ProcessDetails(args),
            _ => throw new ToolException($"Unknown tool '{name}'."),
        };
        return result.ToJsonString(McpServer.JsonOptions);
    }

    // ------------------------------------------------------------------ tools

    private JsonObject SystemOverview(JsonElement args)
    {
        double window = GetWindow(args);
        int top = (int)Math.Clamp(GetNumber(args, "top") ?? 5, 1, 25);
        var (entries, latest) = GetWindowEntries(window);
        var s = latest.Snapshot.System;
        var stats = Aggregate(entries);

        double avgTotal = WeightedAvg(entries, e => e.Snapshot.System.TotalPercent);
        double avgIntr = WeightedAvg(entries, e => e.Snapshot.System.InterruptDpcPercent);
        ulong used = s.TotalPhys - s.AvailPhys;

        var o = Header(entries, latest);
        o["cpu"] = new JsonObject
        {
            ["total_percent_latest"] = R(s.TotalPercent),
            ["total_percent_avg"] = R(avgTotal),
            ["interrupt_dpc_percent_avg"] = R(avgIntr),
            ["logical_processors"] = s.CorePercents.Length,
            ["per_core_percent_latest"] = new JsonArray(s.CorePercents.Select(c => (JsonNode)R(c, 0)!).ToArray()),
            ["busiest_cores"] = new JsonArray(s.CorePercents
                .Select((v, i) => (v, i)).OrderByDescending(t => t.v).Take(Math.Min(4, s.CorePercents.Length))
                .Select(t => (JsonNode)new JsonObject { ["core"] = t.i, ["percent"] = R(t.v, 0) }).ToArray()),
            ["note"] = CpuNote,
        };
        o["memory"] = new JsonObject
        {
            ["total_gb"] = Gb(s.TotalPhys),
            ["in_use_gb"] = Gb(used),
            ["in_use_percent"] = R(s.TotalPhys > 0 ? 100.0 * used / s.TotalPhys : 0),
            ["available_gb"] = Gb(s.AvailPhys),
            ["cached_gb"] = Gb(s.Cached),
            ["committed_gb"] = Gb(s.CommitTotal),
            ["commit_limit_gb"] = Gb(s.CommitLimit),
        };
        o["counts"] = new JsonObject { ["processes"] = s.ProcessCount, ["threads"] = s.ThreadCount, ["handles"] = s.HandleCount };
        o["top_by_cpu"] = new JsonArray(stats.OrderByDescending(p => p.AvgCpu).Take(top).Select(p => (JsonNode)ProcJson(p)).ToArray());
        o["top_by_memory"] = new JsonArray(stats.OrderByDescending(p => p.Latest.PrivateWorkingSet).Take(top).Select(p => (JsonNode)ProcJson(p)).ToArray());
        return o;
    }

    private JsonObject TopProcesses(JsonElement args)
    {
        double window = GetWindow(args);
        int limit = (int)Math.Clamp(GetNumber(args, "limit") ?? 15, 1, 200);
        string sortBy = (GetString(args, "sort_by") ?? "cpu").ToLowerInvariant();
        if (sortBy is not ("cpu" or "memory" or "commit"))
            throw new ToolException("sort_by must be one of: cpu, memory, commit.");
        bool group = GetBool(args, "group_by_name") ?? false;
        string? filter = GetString(args, "name_filter");

        var (entries, latest) = GetWindowEntries(window);
        IEnumerable<ProcStats> stats = Aggregate(entries);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filter = filter.Trim();
            stats = uint.TryParse(filter, out uint fpid)
                ? stats.Where(p => p.Latest.Pid == fpid)
                : stats.Where(p => p.Latest.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var o = Header(entries, latest);
        o["sort_by"] = sortBy;
        var list = stats.ToList();
        o["matching_processes"] = list.Count;

        if (group)
        {
            var groups = list
                .GroupBy(p => p.Latest.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Name = g.First().Latest.Name,
                    Count = g.Count(),
                    Cpu = g.Sum(p => p.AvgCpu),
                    CpuLatest = g.Sum(p => p.LatestCpu),
                    Ws = g.Sum(p => p.Latest.PrivateWorkingSet),
                    Commit = g.Sum(p => p.Latest.Commit),
                    Pids = g.OrderByDescending(p => p.AvgCpu).Select(p => p.Latest.Pid).ToList(),
                });
            groups = sortBy switch
            {
                "memory" => groups.OrderByDescending(g => g.Ws),
                "commit" => groups.OrderByDescending(g => g.Commit),
                _ => groups.OrderByDescending(g => g.Cpu),
            };
            o["groups"] = new JsonArray(groups.Take(limit).Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Name,
                ["process_count"] = g.Count,
                ["cpu_percent_avg"] = R(g.Cpu),
                ["cpu_percent_latest"] = R(g.CpuLatest),
                ["private_memory_mb"] = Mb(g.Ws),
                ["commit_mb"] = Mb(g.Commit),
                ["pids"] = new JsonArray(g.Pids.Take(20).Select(p => (JsonNode)p).ToArray()),
            }).ToArray());
        }
        else
        {
            IEnumerable<ProcStats> sorted = sortBy switch
            {
                "memory" => list.OrderByDescending(p => p.Latest.PrivateWorkingSet),
                "commit" => list.OrderByDescending(p => p.Latest.Commit),
                _ => list.OrderByDescending(p => p.AvgCpu),
            };
            o["processes"] = new JsonArray(sorted.Take(limit).Select(p => (JsonNode)ProcJson(p)).ToArray());
        }
        return o;
    }

    private JsonObject ProcessDetails(JsonElement args)
    {
        double? pidArg = GetNumber(args, "pid");
        if (pidArg is not { } pidD || pidD < 0 || pidD > uint.MaxValue - 1 || pidD != Math.Floor(pidD))
            throw new ToolException("pid is required and must be a process ID.");
        uint pid = (uint)pidD;

        double window = GetWindow(args);
        var (entries, latest) = GetWindowEntries(window);
        var procs = latest.Snapshot.Processes.Where(p => p.Pid != SystemSampler.InterruptsPseudoPid).ToList();
        var target = procs.FirstOrDefault(p => p.Pid == pid)
                     ?? throw new ToolException($"No process with PID {pid} is running (as of the latest sample).");

        var stats = Aggregate(entries).ToDictionary(p => p.Latest.Key);
        var st = stats.TryGetValue(target.Key, out var found) ? found : new ProcStats(target, target.CpuPercent, target.CpuPercent, target.CpuPercent);
        var byPid = procs.ToDictionary(p => p.Pid);

        var o = Header(entries, latest);
        var proc = ProcJson(st);
        proc["cpu_percent_peak"] = R(st.PeakCpu);
        proc["started_utc"] = target.CreateTime > 0 ? DateTime.FromFileTimeUtc(target.CreateTime).ToString("O") : null;
        proc["session_id"] = target.SessionId;

        var (path, cmd, err) = QueryPathAndCommandLine(target);
        proc["executable_path"] = path;
        proc["command_line"] = cmd;
        if (err != null) proc["details_unavailable_reason"] = err;
        o["process"] = proc;

        // Parent chain (validated by create time so a reused PID isn't reported as the parent).
        var chain = new JsonArray();
        var cur = target;
        for (int depth = 0; depth < 32 && byPid.TryGetValue(cur.ParentPid, out var parent)
                                       && parent.Pid != cur.Pid && parent.CreateTime <= cur.CreateTime; depth++)
        {
            chain.Add(new JsonObject { ["pid"] = parent.Pid, ["name"] = parent.Name });
            cur = parent;
        }
        o["parent_chain"] = chain;

        var descendants = ProcessKiller.FindDescendants(procs, new KillTarget(target.Pid, target.CreateTime, target.Name));
        var children = procs.Where(p => p.ParentPid == target.Pid && p.CreateTime >= target.CreateTime && p.Pid != target.Pid)
                            .Select(p => stats.TryGetValue(p.Key, out var s) ? s : new ProcStats(p, p.CpuPercent, p.CpuPercent, p.CpuPercent))
                            .OrderByDescending(p => p.AvgCpu).ToList();
        o["children"] = new JsonArray(children.Take(50).Select(c => (JsonNode)ProcJson(c)).ToArray());

        var treeStats = descendants.Select(d => procs.FirstOrDefault(p => p.Pid == d.Pid && p.CreateTime == d.CreateTime))
                                   .Where(p => p != null).Select(p => stats.TryGetValue(p!.Key, out var s) ? s : null)
                                   .Where(s => s != null).Append(st).ToList();
        o["tree_totals"] = new JsonObject
        {
            ["process_count"] = descendants.Count + 1,
            ["cpu_percent_avg"] = R(treeStats.Sum(s => s!.AvgCpu)),
            ["private_memory_mb"] = Mb(treeStats.Sum(s => s!.Latest.PrivateWorkingSet)),
            ["commit_mb"] = Mb(treeStats.Sum(s => s!.Latest.Commit)),
        };
        return o;
    }

    // ------------------------------------------------------------------ aggregation

    private sealed record ProcStats(ProcessSample Latest, double AvgCpu, double PeakCpu, double LatestCpu);

    private (List<SnapshotHistory.Entry> entries, SnapshotHistory.Entry latest) GetWindowEntries(double seconds)
    {
        var entries = _history.Window(seconds);
        if (entries.Count == 0)
            throw new ToolException("ETaskManager has not collected a sample yet. Try again in a second.");
        return (entries, entries[0]);
    }

    /// <summary>
    /// Per process (alive in the latest sample): CPU averaged over the window, weighted by each sample's interval.
    /// A process absent from older samples (not started yet) counts as 0% there.
    /// </summary>
    private static List<ProcStats> Aggregate(List<SnapshotHistory.Entry> entries)
    {
        var latest = entries[0].Snapshot;
        var acc = new Dictionary<ProcKey, (double sum, double peak)>();
        double totalTime = 0;
        foreach (var e in entries)
        {
            double w = Math.Max(e.Snapshot.IntervalSeconds, 1e-3);
            totalTime += w;
            foreach (var p in e.Snapshot.Processes)
            {
                acc.TryGetValue(p.Key, out var a);
                acc[p.Key] = (a.sum + p.CpuPercent * w, Math.Max(a.peak, p.CpuPercent));
            }
        }

        var result = new List<ProcStats>(latest.Processes.Count);
        foreach (var p in latest.Processes)
        {
            if (p.Pid == SystemSampler.InterruptsPseudoPid) continue;
            var a = acc[p.Key];
            result.Add(new ProcStats(p, totalTime > 0 ? a.sum / totalTime : p.CpuPercent, a.peak, p.CpuPercent));
        }
        return result;
    }

    private static double WeightedAvg(List<SnapshotHistory.Entry> entries, Func<SnapshotHistory.Entry, double> f)
    {
        double sum = 0, total = 0;
        foreach (var e in entries)
        {
            double w = Math.Max(e.Snapshot.IntervalSeconds, 1e-3);
            sum += f(e) * w;
            total += w;
        }
        return total > 0 ? sum / total : 0;
    }

    private static JsonObject Header(List<SnapshotHistory.Entry> entries, SnapshotHistory.Entry latest) => new()
    {
        ["sampled_at_utc"] = latest.TimeUtc.ToString("O"),
        ["sample_age_seconds"] = R((DateTime.UtcNow - latest.TimeUtc).TotalSeconds),
        ["sampling_interval_seconds"] = R(latest.Snapshot.IntervalSeconds, 2),
        ["window_seconds_covered"] = R(entries.Sum(e => e.Snapshot.IntervalSeconds)),
        ["samples_in_window"] = entries.Count,
    };

    private static JsonObject ProcJson(ProcStats p) => new()
    {
        ["pid"] = p.Latest.Pid,
        ["name"] = p.Latest.Name,
        ["cpu_percent_avg"] = R(p.AvgCpu),
        ["cpu_percent_latest"] = R(p.LatestCpu),
        ["private_memory_mb"] = Mb(p.Latest.PrivateWorkingSet),
        ["commit_mb"] = Mb(p.Latest.Commit),
        ["threads"] = p.Latest.Threads,
        ["handles"] = p.Latest.Handles,
        ["cpu_time_seconds"] = R(p.Latest.CpuTime100ns / 10_000_000.0, 1),
        ["parent_pid"] = p.Latest.ParentPid,
    };

    // ------------------------------------------------------------------ process path / command line

    private static unsafe (string? path, string? cmd, string? error) QueryPathAndCommandLine(ProcessSample p)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return (null, null, err == ERROR_ACCESS_DENIED
                ? (ProcessKiller.IsElevated ? "Access denied (protected process)." : "Access denied; run ETaskManager as administrator to see this process's path and command line.")
                : new System.ComponentModel.Win32Exception(err).Message);
        }
        // Make sure the PID still refers to the sampled process instance.
        if (!GetProcessTimes(h, out long create, out _, out _, out _) || create != p.CreateTime)
            return (null, null, "The process exited or its PID was reused.");

        string? path = null;
        var buf = new char[32768];
        uint size = (uint)buf.Length;
        if (QueryFullProcessImageName(h, 0, buf, ref size)) path = new string(buf, 0, (int)size);

        string? cmd = null;
        int len = 64 * 1024;
        for (int attempt = 0; attempt < 3 && cmd == null; attempt++)
        {
            IntPtr mem = Marshal.AllocHGlobal(len);
            try
            {
                int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, mem, len, out int needed);
                if (status == STATUS_SUCCESS)
                {
                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } followed by the text.
                    ushort bytes = *(ushort*)mem;
                    IntPtr text = *(IntPtr*)(mem + 8);
                    cmd = bytes > 0 && text != IntPtr.Zero ? new string((char*)text, 0, bytes / 2) : "";
                }
                else if (status is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL && needed > len) len = needed;
                else break;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
        return (path, cmd, path == null && cmd == null ? "Windows did not return the path or command line." : null);
    }

    // ------------------------------------------------------------------ helpers

    private static double GetWindow(JsonElement args) => Math.Clamp(GetNumber(args, "window_seconds") ?? 10, 0, 300);

    private static double? GetNumber(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
        if (v.ValueKind == JsonValueKind.Null) return null;
        throw new ToolException($"{name} must be a number.");
    }

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }

    private static bool? GetBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out bool b) => b,
            JsonValueKind.Null => null,
            _ => throw new ToolException($"{name} must be true or false."),
        };
    }

    private static double R(double v, int digits = 1) => Math.Round(v, digits);
    private static double Mb(long bytes) => Math.Round(bytes / (1024.0 * 1024), 1);
    private static double Gb(ulong bytes) => Math.Round(bytes / (1024.0 * 1024 * 1024), 2);
}
