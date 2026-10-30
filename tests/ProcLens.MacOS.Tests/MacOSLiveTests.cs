using System.Diagnostics;
using FluentAssertions;
using ProcLens.Core.Models;
using ProcLens.Platform.MacOS;

namespace ProcLens.MacOS.Tests;

/// <summary>Runs the provider against the real macOS host. Skipped on other operating systems.</summary>
public class MacOSLiveTests
{
    [SkippableOnNonMacOS]
    public async Task Inspecting_this_process_matches_what_the_runtime_reports()
    {
        using var self = Process.GetCurrentProcess();

        var info = await new MacOSProcessProvider().GetProcessInfoAsync(self.Id);

        info.Should().NotBeNull();
        info!.StartTime.Should().BeCloseTo(self.StartTime, TimeSpan.FromSeconds(1));
        info.MemoryUsage.Value.Should().BeGreaterThan(0);
        info.ThreadCount.IsAvailable.Should().BeTrue();
        info.ParentProcessId.Should().BeNull("the managed APIs do not expose it; it must be reported as unavailable");
    }

    [SkippableOnNonMacOS]
    public async Task Listing_includes_this_process()
    {
        var all = await new MacOSProcessProvider().GetProcessesAsync();

        all.Should().Contain(p => p.ProcessId == Environment.ProcessId);
        all.Should().OnlyContain(p => !p.CpuUsage.IsAvailable);
    }

    [SkippableOnNonMacOS]
    public async Task Cpu_time_grows_while_this_process_burns_cpu()
    {
        var provider = new MacOSProcessProvider();
        var before = (await provider.GetProcessInfoAsync(Environment.ProcessId))!.CpuTime.Value!.Value;

        var until = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < until) { }

        var after = (await provider.GetProcessInfoAsync(Environment.ProcessId))!.CpuTime.Value!.Value;
        (after - before).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    [SkippableOnNonMacOS]
    public async Task Memory_stats_report_working_set_and_never_substitute_zero()
    {
        var memory = await new MacOSProcessProvider().GetMemoryUsageAsync(Environment.ProcessId);

        memory.Should().NotBeNull();
        memory!.WorkingSet.Value.Should().BeGreaterThan(0);
        foreach (var m in new[] { memory.PrivateMemory, memory.VirtualMemory, memory.PeakWorkingSet })
            if (m.IsAvailable) m.Value.Should().BeGreaterThan(0);
    }

    [SkippableOnNonMacOS]
    public async Task Threads_of_this_process_are_listed_with_unknown_state_not_invented_values()
    {
        var result = await new MacOSProcessProvider().GetThreadsAsync(Environment.ProcessId);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Should().NotBeEmpty();
        result.Items.Should().OnlyContain(t => t.State == ThreadRunState.Unknown);
    }

    [SkippableOnNonMacOS]
    public async Task Modules_request_returns_an_explicit_not_supported_status()
    {
        var result = await new MacOSProcessProvider().GetModulesAsync(Environment.ProcessId);

        result.Status.Should().Be(InspectionStatus.NotSupported);
    }

    [SkippableOnNonMacOS]
    public async Task Unknown_pid_is_reported_as_not_found()
    {
        var provider = new MacOSProcessProvider();
        (await provider.GetProcessInfoAsync(int.MaxValue)).Should().BeNull();
        (await provider.GetMemoryUsageAsync(int.MaxValue)).Should().BeNull();
    }
}

public sealed class SkippableOnNonMacOSAttribute : FactAttribute
{
    public SkippableOnNonMacOSAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS only.";
    }
}
