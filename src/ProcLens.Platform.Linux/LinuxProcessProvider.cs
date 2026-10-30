using ProcLens.Core.Abstractions;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;

namespace ProcLens.Platform.Linux;

public sealed class LinuxProcessProvider(string procRoot = "/proc") : IPlatformProcessProvider
{
    // Assumes the standard USER_HZ of 100; sysconf(_SC_CLK_TCK) would be needed to confirm.
    private const double ClockTicksPerSecond = 100;

    public PlatformKind Platform => PlatformKind.Linux;

    public IReadOnlyList<CapabilityInfo> GetCapabilities() => CapabilityDefaults.InspectionAndMonitoring();

    public Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var taskDir = Path.Combine(procRoot, processId.ToString(), "task");
            try
            {
                if (!Directory.Exists(Path.Combine(procRoot, processId.ToString()))) return InspectionResult<ThreadInfo>.NotFound();

                var threads = new List<ThreadInfo>();
                var seen = 0;
                foreach (var dir in Directory.EnumerateDirectories(taskDir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!int.TryParse(Path.GetFileName(dir), out var tid)) continue;
                    seen++;

                    // A thread can exit between listing and reading; skip it rather than failing.
                    if (ReadOrNull(Path.Combine(dir, "stat")) is not { } text || ProcParser.ParseStat(text) is not { } stat) continue;

                    var (state, detail) = ProcParser.MapThreadState(stat.State);
                    threads.Add(new ThreadInfo(tid, stat.Name, state, detail,
                        Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(stat.CpuTicks / ClockTicksPerSecond))));
                }

                return seen > 0 && threads.Count == 0
                    ? InspectionResult<ThreadInfo>.Denied("Thread information could not be read.")
                    : InspectionResult<ThreadInfo>.Ok(threads.OrderBy(t => t.ThreadId).ToList());
            }
            catch (UnauthorizedAccessException e)
            {
                return InspectionResult<ThreadInfo>.Denied(e.Message);
            }
            catch (DirectoryNotFoundException)
            {
                return InspectionResult<ThreadInfo>.NotFound();
            }
        }, ct);

    public Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var dir = Path.Combine(procRoot, processId.ToString());
            if (!Directory.Exists(dir)) return InspectionResult<ModuleInfo>.NotFound();

            try
            {
                return InspectionResult<ModuleInfo>.Ok(ProcParser.ParseMaps(File.ReadAllText(Path.Combine(dir, "maps"))));
            }
            catch (UnauthorizedAccessException)
            {
                return InspectionResult<ModuleInfo>.Denied("Reading /proc/<pid>/maps requires ptrace-read permission on the target process.");
            }
            catch (IOException)
            {
                // ESRCH (exited) and EACCES both surface as IOException on some kernels.
                return Directory.Exists(dir)
                    ? InspectionResult<ModuleInfo>.Denied("Reading /proc/<pid>/maps was refused.")
                    : InspectionResult<ModuleInfo>.NotFound();
            }
        }, ct);

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessInfo>>(() =>
        {
            var bootTime = ReadBootTime();
            var passwd = ReadOrNull("/etc/passwd");
            var list = new List<ProcessInfo>();
            foreach (var dir in Directory.EnumerateDirectories(procRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (int.TryParse(Path.GetFileName(dir), out var pid) && Read(pid, bootTime, passwd) is { } info)
                    list.Add(info);
            }
            return list;
        }, ct);

    public Task<ProcessInfo?> GetProcessInfoAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() => Read(processId, ReadBootTime(), ReadOrNull("/etc/passwd")), ct);

    public Task<MemoryStats?> GetMemoryUsageAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var dir = Path.Combine(procRoot, processId.ToString());
            if (ReadOrNull(Path.Combine(dir, "stat")) is not { } stat || ProcParser.ParseStat(stat) is null) return null;

            var status = ReadOrNull(Path.Combine(dir, "status")) is { } s ? ProcParser.ParseKeyValues(s) : [];
            Measurement<long> Field(string key) =>
                status.TryGetValue(key, out var v) && ProcParser.ParseKilobytes(v) is { } bytes
                    ? Measurement<long>.Of(bytes)
                    : Measurement<long>.Unavailable($"{key} not reported (kernel thread or access denied).");

            return new MemoryStats(
                WorkingSet: Field("VmRSS"),
                // /proc has no equivalent of Windows private bytes; RssAnon would be a different quantity.
                PrivateMemory: Measurement<long>.Unavailable("Not available on Linux."),
                VirtualMemory: Field("VmSize"),
                PeakWorkingSet: Field("VmHWM"));
        }, ct);

    private ProcessInfo? Read(int pid, DateTimeOffset? bootTime, string? passwd)
    {
        var dir = Path.Combine(procRoot, pid.ToString());
        var stat = ReadOrNull(Path.Combine(dir, "stat")) is { } s ? ProcParser.ParseStat(s) : null;
        if (stat is null) return null; // exited, or not a process directory

        var status = ReadOrNull(Path.Combine(dir, "status")) is { } st ? ProcParser.ParseKeyValues(st) : [];
        status.TryGetValue("VmRSS", out var rss);
        status.TryGetValue("Threads", out var threads);
        var uid = status.TryGetValue("Uid", out var u) ? u.Split('\t', ' ')[0] : null;

        var owner = uid is null ? null : (passwd is null ? null : ProcParser.ResolveUserName(passwd, uid)) ?? uid;
        var path = ExecutablePath(dir);
        var memory = ProcParser.ParseKilobytes(rss);
        var start = bootTime?.AddSeconds(stat.StartTimeTicks / ClockTicksPerSecond);

        var access = path is not null && memory.HasValue ? AccessStatus.Full
            : memory.HasValue || path is not null || threads is not null ? AccessStatus.Partial
            : AccessStatus.Denied;

        return new ProcessInfo(
            pid, stat.Name, path, stat.ParentProcessId, owner, start,
            Measurement<double>.Unavailable(UnavailableReasons.CpuNotSampled),
            Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(stat.CpuTicks / ClockTicksPerSecond)),
            memory.HasValue ? Measurement<long>.Of(memory.Value) : Measurement<long>.Unavailable("VmRSS not reported (kernel thread or access denied)."),
            int.TryParse(threads, out var t) ? Measurement<int>.Of(t) : Measurement<int>.Unavailable("Threads not reported."),
            PlatformKind.Linux, access);
    }

    private DateTimeOffset? ReadBootTime()
    {
        var stat = ReadOrNull(Path.Combine(procRoot, "stat"));
        var line = stat?.Split('\n').FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal));
        return line is not null && long.TryParse(line.AsSpan(6), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private static string? ExecutablePath(string procDir)
    {
        try { return new FileInfo(Path.Combine(procDir, "exe")).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

