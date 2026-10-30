using ProcLens.Application.Abstractions;
using ProcLens.Application.Monitoring;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;

namespace ProcLens.Application.Processes;

public enum ProcessSortKey
{
    Pid,
    Name,
    Memory,
    Cpu,
}

public sealed record ProcessListOptions(
    ProcessSortKey Sort = ProcessSortKey.Pid,
    string? NameFilter = null,
    TimeSpan? CpuSampleInterval = null);

public sealed class ProcessQueryService(
    IPlatformProcessProvider provider,
    ICapabilityRegistry capabilities,
    TimeProvider? timeProvider = null) : IProcessQueryService
{
    private static readonly TimeSpan DefaultCpuSampleInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<ProcessInfo>> ListAsync(ProcessListOptions options, CancellationToken ct = default)
    {
        CapabilityGuard.Require(capabilities, Capability.ProcessInspection);

        IEnumerable<ProcessInfo> items;
        if (options.Sort == ProcessSortKey.Cpu)
        {
            CapabilityGuard.Require(capabilities, Capability.CpuMonitoring);
            items = await SampleCpuAsync(options.CpuSampleInterval ?? DefaultCpuSampleInterval, ct);
        }
        else
        {
            items = await provider.GetProcessesAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(options.NameFilter))
            items = items.Where(p => p.ProcessName.Contains(options.NameFilter, StringComparison.OrdinalIgnoreCase));

        // Processes whose value is unavailable sort last rather than being treated as zero.
        return options.Sort switch
        {
            ProcessSortKey.Name => items.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ToList(),
            ProcessSortKey.Memory => items.OrderByDescending(p => p.MemoryUsage.IsAvailable)
                .ThenByDescending(p => p.MemoryUsage.Value ?? 0).ToList(),
            ProcessSortKey.Cpu => items.OrderByDescending(p => p.CpuUsage.IsAvailable)
                .ThenByDescending(p => p.CpuUsage.Value ?? 0).ToList(),
            _ => items.OrderBy(p => p.ProcessId).ToList(),
        };
    }

    public Task<ProcessInfo?> InspectAsync(int processId, CancellationToken ct = default)
    {
        CapabilityGuard.Require(capabilities, Capability.ProcessInspection);
        return provider.GetProcessInfoAsync(processId, ct);
    }

    public Task<MemoryStats?> GetMemoryAsync(int processId, CancellationToken ct = default)
    {
        CapabilityGuard.Require(capabilities, Capability.MemoryMonitoring);
        return provider.GetMemoryUsageAsync(processId, ct);
    }

    public Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default)
    {
        CapabilityGuard.Require(capabilities, Capability.ThreadInspection);
        return provider.GetThreadsAsync(processId, ct);
    }

    public Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default)
    {
        CapabilityGuard.Require(capabilities, Capability.ModuleInspection);
        return provider.GetModulesAsync(processId, ct);
    }

    private async Task<IReadOnlyList<ProcessInfo>> SampleCpuAsync(TimeSpan interval, CancellationToken ct)
    {
        var first = await provider.GetProcessesAsync(ct);
        var firstAt = _time.GetTimestamp();
        var previous = new Dictionary<int, ProcessInfo>();
        foreach (var p in first) previous[p.ProcessId] = p;

        await Task.Delay(interval, _time, ct);

        var second = await provider.GetProcessesAsync(ct);
        var elapsed = _time.GetElapsedTime(firstAt);

        return second.Select(p =>
        {
            if (!previous.TryGetValue(p.ProcessId, out var before)) return p;
            return CpuUsageCalculator.Compute(before, p, elapsed, Environment.ProcessorCount) is { } cpu
                ? p with { CpuUsage = Measurement<double>.Of(cpu.PercentOfMachine) }
                : p;
        }).ToList();
    }
}
