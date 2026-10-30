using FluentAssertions;
using System.Diagnostics;
using ProcLens.Core.Models;
using ProcLens.Platform.Windows;

namespace ProcLens.Windows.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsProcessProviderTests
{
    [SkippableOnNonWindows]
    public async Task Inspecting_the_current_process_returns_matching_identity()
    {
        using var self = Process.GetCurrentProcess();

        var info = await new WindowsProcessProvider().GetProcessInfoAsync(self.Id);

        info.Should().NotBeNull();
        info!.ProcessId.Should().Be(self.Id);
        info.StartTime.Should().BeCloseTo(self.StartTime, TimeSpan.FromSeconds(1));
        info.ExecutablePath.Should().NotBeNullOrEmpty();
        info.ParentProcessId.Should().NotBeNull();
        info.MemoryUsage.IsAvailable.Should().BeTrue();
        info.AccessStatus.Should().Be(AccessStatus.Full);
    }

    [SkippableOnNonWindows]
    public async Task Listing_includes_the_current_process_and_marks_cpu_unavailable_not_zero()
    {
        var all = await new WindowsProcessProvider().GetProcessesAsync();

        all.Should().Contain(p => p.ProcessId == Environment.ProcessId);
        all.Should().OnlyContain(p => !p.CpuUsage.IsAvailable);
        all.Single(p => p.ProcessId == Environment.ProcessId).CpuTime.IsAvailable.Should().BeTrue();
    }

    [SkippableOnNonWindows]
    public async Task Owner_of_the_current_process_is_the_current_user()
    {
        var info = await new WindowsProcessProvider().GetProcessInfoAsync(Environment.ProcessId);

        info!.Owner.Should().Be(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
    }

    [SkippableOnNonWindows]
    public async Task Memory_stats_for_the_current_process_are_all_available_and_consistent()
    {
        var memory = await new WindowsProcessProvider().GetMemoryUsageAsync(Environment.ProcessId);

        memory.Should().NotBeNull();
        memory!.WorkingSet.IsAvailable.Should().BeTrue();
        memory.PrivateMemory.IsAvailable.Should().BeTrue();
        memory.VirtualMemory.IsAvailable.Should().BeTrue();
        memory.PeakWorkingSet.Value.Should().BeGreaterThanOrEqualTo(memory.WorkingSet.Value!.Value);
    }

    [SkippableOnNonWindows]
    public async Task Cpu_time_grows_while_the_process_burns_cpu_and_matches_the_framework()
    {
        var provider = new WindowsProcessProvider();
        var before = (await provider.GetProcessInfoAsync(Environment.ProcessId))!.CpuTime.Value!.Value;

        var until = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < until) { }

        var after = (await provider.GetProcessInfoAsync(Environment.ProcessId))!.CpuTime.Value!.Value;
        (after - before).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    [SkippableOnNonWindows]
    public async Task Exited_process_is_reported_as_not_found()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        var pid = child.Id;
        await child.WaitForExitAsync();
        var provider = new WindowsProcessProvider();

        // child still holds an open handle here, so GetProcessById succeeds; HasExited must hide it.
        (await provider.GetProcessInfoAsync(pid)).Should().BeNull();
        (await provider.GetMemoryUsageAsync(pid)).Should().BeNull();
    }

    [SkippableOnNonWindows]
    public async Task Threads_of_the_current_process_overlap_the_runtime_view_and_have_cpu_time()
    {
        using var self = Process.GetCurrentProcess();

        var result = await new WindowsProcessProvider().GetThreadsAsync(self.Id);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Select(t => t.ThreadId).Intersect(self.Threads.Cast<ProcessThread>().Select(t => t.Id)).Should().NotBeEmpty();
        result.Items.Should().Contain(t => t.CpuTime.IsAvailable);
        result.Items.Where(t => t.State == ThreadRunState.Waiting).Should().OnlyContain(t => t.StateDetail != null);
    }

    [SkippableOnNonWindows]
    public async Task Modules_of_the_current_process_include_ntdll_and_kernel32_with_addresses()
    {
        var result = await new WindowsProcessProvider().GetModulesAsync(Environment.ProcessId);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Should().Contain(m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase));
        result.Items.Should().Contain(m => m.Name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase));
        result.Items.Should().OnlyContain(m => m.BaseAddress.Value > 0 && m.Size.Value > 0 && m.Path != null);
    }

    [Theory]
    [InlineData(true, false, false, true)]   // 32-bit inspector, 64-bit target: the only failing combination
    [InlineData(true, false, true, false)]   // 32-bit inspector, 32-bit target: fine
    [InlineData(true, true, true, false)]    // 64-bit inspector, 32-bit target: works (WOW64 modules included)
    [InlineData(true, true, false, false)]
    [InlineData(true, false, null, false)]   // bitness unknown: never guess a mismatch
    [InlineData(false, false, false, false)] // 32-bit OS: every process is 32-bit
    public void Architecture_mismatch_only_when_a_32_bit_inspector_faces_a_known_64_bit_target(
        bool osIs64, bool callerIs64, bool? targetIsWow64, bool expected) =>
        WindowsProcessProvider.IsArchitectureMismatch(osIs64, callerIs64, targetIsWow64).Should().Be(expected);

    [SkippableOnNonWindows]
    public async Task A_64_bit_inspector_can_list_the_modules_of_a_32_bit_process()
    {
        var wow64Ping = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SysWOW64\PING.EXE");
        if (!Environment.Is64BitProcess || !File.Exists(wow64Ping)) return; // needs a 64-bit host on 64-bit Windows

        using var child = Process.Start(new ProcessStartInfo(wow64Ping, "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            await Task.Delay(500); // let the loader finish
            var result = await new WindowsProcessProvider().GetModulesAsync(child.Id);

            result.Status.Should().Be(InspectionStatus.Available);
            result.Items.Should().Contain(m => m.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
            result.Items.Should().Contain(m => m.Name.Equals("wow64.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally { child.Kill(); }
    }

    [SkippableOnNonWindows]
    public async Task Modules_of_the_protected_system_process_are_denied_not_empty()
    {
        var result = await new WindowsProcessProvider().GetModulesAsync(4);

        result.Status.Should().Be(InspectionStatus.AccessDenied);
        result.Items.Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Unknown_pid_is_not_found_for_threads_and_modules()
    {
        var provider = new WindowsProcessProvider();

        (await provider.GetThreadsAsync(int.MaxValue)).Status.Should().Be(InspectionStatus.ProcessNotFound);
        (await provider.GetModulesAsync(int.MaxValue)).Status.Should().Be(InspectionStatus.ProcessNotFound);
    }

    [SkippableOnNonWindows]
    public async Task Unknown_pid_returns_null()
    {
        (await new WindowsProcessProvider().GetProcessInfoAsync(int.MaxValue)).Should().BeNull();
    }
}

/// <summary>A [Fact] that is skipped (not failed) when not running on Windows.</summary>
public sealed class SkippableOnNonWindowsAttribute : FactAttribute
{
    public SkippableOnNonWindowsAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only.";
    }
}



