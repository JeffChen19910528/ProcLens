using System.Runtime.CompilerServices;
using ProcLens.Application.Abstractions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Platform;

namespace ProcLens.Application.Monitoring;

public sealed class ProcessMonitor(
    IPlatformProcessProvider provider,
    ICapabilityRegistry capabilities,
    TimeProvider? timeProvider = null) : IProcessMonitor
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Samples a process until it exits, its PID is reused, <paramref name="maxSamples"/> is reached, or the token is cancelled.
    /// Yields nothing if the process does not exist at the start.
    /// </summary>
    public async IAsyncEnumerable<MonitorSample> MonitorAsync(
        int processId,
        TimeSpan interval,
        MonitorThresholds? thresholds = null,
        int? maxSamples = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CapabilityGuard.Require(capabilities, Capability.CpuMonitoring);
        CapabilityGuard.Require(capabilities, Capability.MemoryMonitoring);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        var analyzer = new MonitorAnalyzer(Environment.ProcessorCount, thresholds);
        DateTimeOffset? startTime = null;
        long lastTimestamp = 0;
        var count = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var timestamp = _time.GetTimestamp();
            var info = await provider.GetProcessInfoAsync(processId, ct);
            var memory = info is null ? null : await provider.GetMemoryUsageAsync(processId, ct);
            if (info is null || memory is null) yield break; // exited

            if (count == 0) startTime = info.StartTime;
            else if (info.StartTime != startTime) yield break; // PID reused by a different process

            var elapsed = count == 0 ? TimeSpan.Zero : _time.GetElapsedTime(lastTimestamp, timestamp);
            lastTimestamp = timestamp;
            yield return analyzer.Add(_time.GetLocalNow(), info, memory, elapsed);

            count++;
            if (maxSamples is { } max && count >= max) yield break;
            await Task.Delay(interval, _time, ct);
        }
    }
}

