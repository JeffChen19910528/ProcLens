using ProcLens.Core.Models;

namespace ProcLens.Application.Monitoring;

public static class CpuUsageCalculator
{
    /// <summary>
    /// CPU usage between two samples of the same process. Returns null when it cannot be measured honestly:
    /// no elapsed time, an unreadable CPU time, an unverifiable or changed identity (PID reuse), or a decreasing counter.
    /// </summary>
    public static CpuUsageSample? Compute(ProcessInfo previous, ProcessInfo current, TimeSpan elapsed, int logicalProcessors)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0) return null;
        if (previous.ProcessId != current.ProcessId) return null;
        if (previous.StartTime is null || previous.StartTime != current.StartTime) return null;
        if (previous.CpuTime.Value is not { } before || current.CpuTime.Value is not { } after) return null;

        var delta = after - before;
        if (delta < TimeSpan.Zero) return null;

        var oneCore = delta / elapsed * 100.0;
        return new CpuUsageSample(oneCore, oneCore / logicalProcessors);
    }
}
