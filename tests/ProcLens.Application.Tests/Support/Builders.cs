using ProcLens.Core.Abstractions;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;

namespace ProcLens.Application.Tests.Support;

internal static class Builders
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static ProcessInfo Proc(int pid, string name = "p", long? mem = 100, double? cpuSeconds = 0, DateTimeOffset? start = null) => new(
        pid, name, null, null, null, start ?? Start,
        Measurement<double>.Unavailable("not sampled"),
        cpuSeconds is { } s ? Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(s)) : Measurement<TimeSpan>.Unavailable("denied"),
        mem is { } m ? Measurement<long>.Of(m) : Measurement<long>.Unavailable("denied"),
        Measurement<int>.Of(1), PlatformKind.Linux, AccessStatus.Full);

    public static MemoryStats Mem(long? workingSet) => new(
        workingSet is { } w ? Measurement<long>.Of(w) : Measurement<long>.Unavailable("denied"),
        Measurement<long>.Unavailable("n/a"), Measurement<long>.Of(1), Measurement<long>.Of(1));
}

/// <summary>Provider whose answers come from a queue of scripted snapshots; the last one repeats.</summary>
internal sealed class ScriptedProvider(
    IEnumerable<IReadOnlyList<ProcessInfo>> listSnapshots,
    IEnumerable<ProcessInfo?>? singleSnapshots = null,
    IEnumerable<MemoryStats?>? memorySnapshots = null) : IPlatformProcessProvider
{
    private readonly Queue<IReadOnlyList<ProcessInfo>> _lists = new(listSnapshots);
    private readonly Queue<ProcessInfo?> _singles = new(singleSnapshots ?? []);
    private readonly Queue<MemoryStats?> _memory = new(memorySnapshots ?? []);

    public PlatformKind Platform => PlatformKind.Linux;
    public IReadOnlyList<CapabilityInfo> GetCapabilities() => CapabilityDefaults.InspectionAndMonitoring();

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult(_lists.Count > 1 ? _lists.Dequeue() : _lists.Peek());

    public Task<ProcessInfo?> GetProcessInfoAsync(int processId, CancellationToken ct = default) =>
        Task.FromResult(_singles.Count > 0 ? _singles.Dequeue() : null);

    public Task<MemoryStats?> GetMemoryUsageAsync(int processId, CancellationToken ct = default) =>
        Task.FromResult(_memory.Count > 0 ? _memory.Dequeue() : null);

    public InspectionResult<ThreadInfo> Threads { get; init; } = InspectionResult<ThreadInfo>.Ok([]);
    public InspectionResult<ModuleInfo> Modules { get; init; } = InspectionResult<ModuleInfo>.Ok([]);

    public Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default) => Task.FromResult(Threads);
    public Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default) => Task.FromResult(Modules);
}
