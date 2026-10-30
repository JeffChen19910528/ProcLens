using System.Diagnostics;
using FluentAssertions;
using ProcLens.Core.Models;
using ProcLens.Platform.Linux;

namespace ProcLens.Linux.Tests;

/// <summary>Runs the provider against the real /proc of the host. Skipped on other operating systems.</summary>
public class LinuxLiveTests
{
    [SkippableOnNonLinux]
    public async Task Inspecting_this_process_matches_what_the_runtime_reports()
    {
        using var self = Process.GetCurrentProcess();

        var info = await new LinuxProcessProvider().GetProcessInfoAsync(self.Id);

        info.Should().NotBeNull();
        info!.ExecutablePath.Should().Be(Environment.ProcessPath);
        // Cross-checks the boot-time + USER_HZ conversion against the runtime's own start time.
        info.StartTime.Should().BeCloseTo(self.StartTime, TimeSpan.FromSeconds(1));
        info.Owner.Should().NotBeNullOrEmpty();
        info.ParentProcessId.Should().BeGreaterThan(0);
        info.MemoryUsage.Value.Should().BeGreaterThan(0);
        info.ThreadCount.Value.Should().BeGreaterThan(0);
        info.AccessStatus.Should().Be(AccessStatus.Full);
    }

    [SkippableOnNonLinux]
    public async Task Listing_includes_this_process_and_pid_1()
    {
        var all = await new LinuxProcessProvider().GetProcessesAsync();

        all.Should().Contain(p => p.ProcessId == Environment.ProcessId);
        all.Should().Contain(p => p.ProcessId == 1);
    }

    [SkippableOnNonLinux]
    public async Task Cpu_time_grows_while_this_process_burns_cpu()
    {
        var provider = new LinuxProcessProvider();
        var before = (await provider.GetProcessInfoAsync(Environment.ProcessId))!.CpuTime.Value!.Value;

        var until = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < until) { }

        var after = (await provider.GetProcessInfoAsync(Environment.ProcessId))!.CpuTime.Value!.Value;
        (after - before).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    [SkippableOnNonLinux]
    public async Task Memory_stats_are_consistent_and_private_memory_is_unavailable_not_zero()
    {
        var memory = await new LinuxProcessProvider().GetMemoryUsageAsync(Environment.ProcessId);

        memory!.WorkingSet.Value.Should().BeGreaterThan(0);
        memory.PeakWorkingSet.Value.Should().BeGreaterThanOrEqualTo(memory.WorkingSet.Value!.Value);
        memory.VirtualMemory.Value.Should().BeGreaterThanOrEqualTo(memory.WorkingSet.Value!.Value);
        memory.PrivateMemory.IsAvailable.Should().BeFalse();
    }

    [SkippableOnNonLinux]
    public async Task Exited_or_unknown_process_is_reported_as_not_found()
    {
        var provider = new LinuxProcessProvider();
        (await provider.GetProcessInfoAsync(int.MaxValue)).Should().BeNull();
        (await provider.GetMemoryUsageAsync(int.MaxValue)).Should().BeNull();
    }
}

public sealed class SkippableOnNonLinuxAttribute : FactAttribute
{
    public SkippableOnNonLinuxAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only.";
    }
}
