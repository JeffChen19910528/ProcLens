using System.Diagnostics;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using static ProcLens.Core.Models.Probe;

namespace ProcLens.Platform.MacOS;

public sealed class MacOSProcessProvider : IPlatformProcessProvider
{
    public PlatformKind Platform => PlatformKind.MacOS;

    private const string ModulesUnsupported =
        "Loaded modules are not exposed by managed APIs on macOS; native dyld enumeration is not implemented.";

    public IReadOnlyList<CapabilityInfo> GetCapabilities() => CapabilityDefaults.InspectionAndMonitoring(ModulesUnsupported);

    public Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default) =>
        Task.FromResult(InspectionResult<ModuleInfo>.NotSupported(ModulesUnsupported));

    public Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return InspectionResult<ThreadInfo>.NotFound(); }

            using (process)
            {
                try
                {
                    var threads = new List<ThreadInfo>();
                    foreach (ProcessThread thread in process.Threads)
                    {
                        ct.ThrowIfCancellationRequested();
                        var cpu = TryGet(() => thread.TotalProcessorTime);
                        threads.Add(new ThreadInfo(
                            thread.Id, Name: null, ThreadRunState.Unknown, StateDetail: null,
                            cpu.HasValue ? Measurement<TimeSpan>.Of(cpu.Value) : Measurement<TimeSpan>.Unavailable("Not reported on macOS.")));
                    }
                    return InspectionResult<ThreadInfo>.Ok(threads.OrderBy(t => t.ThreadId).ToList());
                }
                catch (InvalidOperationException) { return InspectionResult<ThreadInfo>.NotFound(); }
                catch (System.ComponentModel.Win32Exception e) { return InspectionResult<ThreadInfo>.Denied(e.Message); }
                catch (NotSupportedException e) { return InspectionResult<ThreadInfo>.NotSupported(e.Message); }
            }
        }, ct);

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessInfo>>(() =>
        {
            var list = new List<ProcessInfo>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(Build(process));
                }
            }
            return list;
        }, ct);

    public Task<ProcessInfo?> GetProcessInfoAsync(int processId, CancellationToken ct = default) =>
        Task.Run<ProcessInfo?>(() =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return TryGet(() => process.HasExited) == true ? null : Build(process);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }, ct);

    public Task<MemoryStats?> GetMemoryUsageAsync(int processId, CancellationToken ct = default) =>
        Task.Run<MemoryStats?>(() =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return new MemoryStats(
                    Read(() => process.WorkingSet64),
                    Read(() => process.PrivateMemorySize64),
                    Read(() => process.VirtualMemorySize64),
                    Read(() => process.PeakWorkingSet64));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }, ct);

    private static Measurement<long> Read(Func<long> read)
    {
        var value = TryGet(read);
        return value.HasValue ? Measurement<long>.Of(value.Value) : Measurement<long>.Unavailable(UnavailableReasons.AccessDenied);
    }

    private static ProcessInfo Build(Process process)
    {
        var start = TryGet(() => (DateTimeOffset)process.StartTime);
        var cpuTime = TryGet(() => process.TotalProcessorTime);
        var memory = TryGet(() => process.WorkingSet64);
        var threads = TryGet(() => process.Threads.Count);
        var path = TryGetRef(() => process.MainModule?.FileName);

        var readable = new[] { start.HasValue, memory.HasValue, threads.HasValue, path is not null };
        var access = readable.All(r => r) ? AccessStatus.Full : readable.Any(r => r) ? AccessStatus.Partial : AccessStatus.Denied;

        return new ProcessInfo(
            process.Id, process.ProcessName, path,
            ParentProcessId: null, // not exposed by managed APIs on macOS
            Owner: null,
            StartTime: start,
            CpuUsage: Measurement<double>.Unavailable(UnavailableReasons.CpuNotSampled),
            CpuTime: cpuTime.HasValue ? Measurement<TimeSpan>.Of(cpuTime.Value) : Measurement<TimeSpan>.Unavailable(UnavailableReasons.AccessDenied),
            MemoryUsage: memory.HasValue ? Measurement<long>.Of(memory.Value) : Measurement<long>.Unavailable(UnavailableReasons.AccessDenied),
            ThreadCount: threads.HasValue ? Measurement<int>.Of(threads.Value) : Measurement<int>.Unavailable(UnavailableReasons.AccessDenied),
            Platform: PlatformKind.MacOS,
            AccessStatus: access);
    }

}

