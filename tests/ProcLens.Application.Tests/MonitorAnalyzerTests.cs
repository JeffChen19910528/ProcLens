using FluentAssertions;
using ProcLens.Application.Monitoring;
using static ProcLens.Application.Tests.Support.Builders;

namespace ProcLens.Application.Tests;

public class MonitorAnalyzerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void First_sample_has_unavailable_cpu_not_zero()
    {
        var sample = new MonitorAnalyzer(4).Add(Start, Proc(1), Mem(100), TimeSpan.Zero);

        sample.CpuPercentOfOneCore.IsAvailable.Should().BeFalse();
        sample.AverageCpuPercentOfMachine.IsAvailable.Should().BeFalse();
        sample.Process.CpuUsage.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Running_average_covers_all_measured_intervals()
    {
        var analyzer = new MonitorAnalyzer(1);
        analyzer.Add(Start, Proc(1, cpuSeconds: 0), Mem(100), TimeSpan.Zero);
        analyzer.Add(Start, Proc(1, cpuSeconds: 0.2), Mem(100), Second);          // 20%
        var last = analyzer.Add(Start, Proc(1, cpuSeconds: 0.8), Mem(100), Second); // 60%

        last.AverageCpuPercentOfMachine.Value.Should().BeApproximately(40, 0.001);
        last.Process.CpuUsage.Value.Should().BeApproximately(60, 0.001);
    }

    [Fact]
    public void High_cpu_raises_a_warning_only_at_or_above_the_threshold()
    {
        var analyzer = new MonitorAnalyzer(1, new MonitorThresholds(HighCpuPercentOfMachine: 50));
        analyzer.Add(Start, Proc(1, cpuSeconds: 0), Mem(100), TimeSpan.Zero);

        analyzer.Add(Start, Proc(1, cpuSeconds: 0.49), Mem(100), Second).Warnings.Should().BeEmpty();
        analyzer.Add(Start, Proc(1, cpuSeconds: 1.1), Mem(100), Second).Warnings.Should().ContainSingle(w => w.Code == "HIGH_CPU");
    }

    [Fact]
    public void Steady_memory_growth_over_the_window_raises_a_warning()
    {
        var analyzer = new MonitorAnalyzer(1, new MonitorThresholds(GrowthWindow: 4, GrowthPercent: 10));
        MonitorSampleResult(analyzer, 100, 105, 110).Warnings.Should().BeEmpty(); // window not full yet
        analyzer.Add(Start, Proc(1), Mem(120), Second).Warnings.Should().ContainSingle(w => w.Code == "MEMORY_GROWTH");
    }

    [Fact]
    public void Fluctuating_or_slow_memory_does_not_raise_a_warning()
    {
        var wobbly = new MonitorAnalyzer(1, new MonitorThresholds(GrowthWindow: 4));
        MonitorSampleResult(wobbly, 100, 150, 120);
        wobbly.Add(Start, Proc(1), Mem(200), Second).Warnings.Should().BeEmpty();

        var slow = new MonitorAnalyzer(1, new MonitorThresholds(GrowthWindow: 4, GrowthPercent: 10));
        MonitorSampleResult(slow, 100, 101, 102);
        slow.Add(Start, Proc(1), Mem(103), Second).Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Unavailable_working_set_is_ignored_instead_of_counting_as_zero()
    {
        var analyzer = new MonitorAnalyzer(1, new MonitorThresholds(GrowthWindow: 2, GrowthPercent: 10));
        analyzer.Add(Start, Proc(1), Mem(100), Second);
        analyzer.Add(Start, Proc(1), Mem(null), Second).Warnings.Should().BeEmpty();
        analyzer.Add(Start, Proc(1), Mem(150), Second).Warnings.Should().ContainSingle(w => w.Code == "MEMORY_GROWTH");
    }

    private static ProcLens.Application.Monitoring.MonitorSample MonitorSampleResult(MonitorAnalyzer a, params long[] values)
    {
        ProcLens.Application.Monitoring.MonitorSample? last = null;
        foreach (var v in values) last = a.Add(Start, Proc(1), Mem(v), Second);
        return last!;
    }
}
