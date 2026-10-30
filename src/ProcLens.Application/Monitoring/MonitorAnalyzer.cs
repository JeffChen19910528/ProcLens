using ProcLens.Core.Models;

namespace ProcLens.Application.Monitoring;

public sealed record MonitorThresholds(double HighCpuPercentOfMachine = 80, int GrowthWindow = 5, double GrowthPercent = 10);

public sealed record MonitorSample(
    DateTimeOffset Timestamp,
    ProcessInfo Process,
    MemoryStats Memory,
    Measurement<double> CpuPercentOfOneCore,
    Measurement<double> AverageCpuPercentOfMachine,
    IReadOnlyList<DiagnosticWarning> Warnings);

/// <summary>Accumulates samples of one process and derives CPU usage, running average and abnormal-usage warnings.</summary>
public sealed class MonitorAnalyzer(int logicalProcessors, MonitorThresholds? thresholds = null)
{
    private readonly MonitorThresholds _thresholds = thresholds ?? new MonitorThresholds();
    private readonly Queue<long> _workingSets = new();
    private ProcessInfo? _previous;
    private double _cpuSum;
    private int _cpuCount;

    public MonitorSample Add(DateTimeOffset at, ProcessInfo info, MemoryStats memory, TimeSpan elapsedSincePrevious)
    {
        var warnings = new List<DiagnosticWarning>();
        var cpu = _previous is null ? null : CpuUsageCalculator.Compute(_previous, info, elapsedSincePrevious, logicalProcessors);
        _previous = info;

        Measurement<double> oneCore, average;
        if (cpu is { } c)
        {
            _cpuSum += c.PercentOfMachine;
            _cpuCount++;
            oneCore = Measurement<double>.Of(c.PercentOfOneCore);
            average = Measurement<double>.Of(_cpuSum / _cpuCount);
            info = info with { CpuUsage = Measurement<double>.Of(c.PercentOfMachine) };

            if (c.PercentOfMachine >= _thresholds.HighCpuPercentOfMachine)
                warnings.Add(new("HIGH_CPU", $"CPU usage {c.PercentOfMachine:F1}% of machine is at or above {_thresholds.HighCpuPercentOfMachine:F0}%."));
        }
        else
        {
            var reason = _cpuCount == 0 && elapsedSincePrevious == TimeSpan.Zero
                ? "First sample; CPU usage needs two samples."
                : "CPU time not readable, or process identity could not be verified.";
            oneCore = Measurement<double>.Unavailable(reason);
            average = _cpuCount > 0 ? Measurement<double>.Of(_cpuSum / _cpuCount) : Measurement<double>.Unavailable(reason);
        }

        TrackMemoryGrowth(memory, warnings);
        return new MonitorSample(at, info, memory, oneCore, average, warnings);
    }

    private void TrackMemoryGrowth(MemoryStats memory, List<DiagnosticWarning> warnings)
    {
        if (memory.WorkingSet.Value is not { } ws) return;

        var window = Math.Max(2, _thresholds.GrowthWindow);
        _workingSets.Enqueue(ws);
        while (_workingSets.Count > window) _workingSets.Dequeue();
        if (_workingSets.Count < window) return;

        var values = _workingSets.ToArray();
        var rising = values.Zip(values.Skip(1), (a, b) => b > a).All(x => x);
        if (!rising || values[0] <= 0) return;

        var growth = (values[^1] - values[0]) * 100.0 / values[0];
        if (growth >= _thresholds.GrowthPercent)
            warnings.Add(new("MEMORY_GROWTH",
                $"Working set rose every sample for {window} samples (+{growth:F1}%); possible memory growth."));
    }
}
