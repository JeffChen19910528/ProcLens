using ProcLens.Core.Models;
using ProcLens.Core.Platform;

namespace ProcLens.Core.Abstractions;

public interface IPlatformProcessProvider
{
    PlatformKind Platform { get; }
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default);
    Task<ProcessInfo?> GetProcessInfoAsync(int processId, CancellationToken ct = default);

    /// <returns>Null if the process does not exist.</returns>
    Task<MemoryStats?> GetMemoryUsageAsync(int processId, CancellationToken ct = default);

    Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default);
    Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default);

    IReadOnlyList<CapabilityInfo> GetCapabilities();
}
