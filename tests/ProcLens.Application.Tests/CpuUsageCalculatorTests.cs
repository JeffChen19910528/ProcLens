using FluentAssertions;
using ProcLens.Application.Monitoring;
using static ProcLens.Application.Tests.Support.Builders;

namespace ProcLens.Application.Tests;

public class CpuUsageCalculatorTests
{
    [Fact]
    public void One_busy_core_is_100_percent_of_a_core_and_divided_across_logical_processors()
    {
        var result = CpuUsageCalculator.Compute(Proc(1, cpuSeconds: 10), Proc(1, cpuSeconds: 12), TimeSpan.FromSeconds(2), 8);

        result!.Value.PercentOfOneCore.Should().BeApproximately(100, 0.001);
        result.Value.PercentOfMachine.Should().BeApproximately(12.5, 0.001);
    }

    [Fact]
    public void Multi_threaded_usage_can_exceed_one_core()
    {
        var result = CpuUsageCalculator.Compute(Proc(1, cpuSeconds: 0), Proc(1, cpuSeconds: 3), TimeSpan.FromSeconds(1), 4);

        result!.Value.PercentOfOneCore.Should().BeApproximately(300, 0.001);
        result.Value.PercentOfMachine.Should().BeApproximately(75, 0.001);
    }

    [Fact]
    public void Idle_process_is_a_real_zero_not_unavailable()
    {
        var result = CpuUsageCalculator.Compute(Proc(1, cpuSeconds: 5), Proc(1, cpuSeconds: 5), TimeSpan.FromSeconds(1), 4);

        result.Should().NotBeNull();
        result!.Value.PercentOfMachine.Should().Be(0);
    }

    [Fact]
    public void Different_start_time_means_the_pid_was_reused_and_no_value_is_produced() =>
        CpuUsageCalculator.Compute(Proc(1, cpuSeconds: 100), Proc(1, cpuSeconds: 1, start: Start.AddMinutes(5)), TimeSpan.FromSeconds(1), 4)
            .Should().BeNull();

    [Fact]
    public void Unverifiable_identity_yields_null() =>
        CpuUsageCalculator.Compute(Proc(1) with { StartTime = null }, Proc(1) with { StartTime = null }, TimeSpan.FromSeconds(1), 4)
            .Should().BeNull();

    [Fact]
    public void Unreadable_cpu_time_decreasing_counter_or_no_elapsed_time_yield_null()
    {
        var one = TimeSpan.FromSeconds(1);
        CpuUsageCalculator.Compute(Proc(1, cpuSeconds: null), Proc(1, cpuSeconds: 2), one, 4).Should().BeNull();
        CpuUsageCalculator.Compute(Proc(1, cpuSeconds: 5), Proc(1, cpuSeconds: 2), one, 4).Should().BeNull();
        CpuUsageCalculator.Compute(Proc(1, cpuSeconds: 1), Proc(1, cpuSeconds: 2), TimeSpan.Zero, 4).Should().BeNull();
    }
}
