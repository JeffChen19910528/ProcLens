using FluentAssertions;
using ProcLens.Application;
using ProcLens.Application.Monitoring;
using ProcLens.Application.Processes;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Application.Tests.Support;
using static ProcLens.Application.Tests.Support.Builders;

namespace ProcLens.Application.Tests;

public class ProcessQueryServiceTests
{
    private static ProcessQueryService Service(ScriptedProvider provider, IEnumerable<CapabilityInfo>? caps = null) =>
        new(provider, new CapabilityRegistry(PlatformKind.Linux, caps ?? provider.GetCapabilities()));

    private static ScriptedProvider ListOf(params ProcessInfo[] items) => new([items]);

    [Fact]
    public async Task Memory_sort_puts_unavailable_values_last_instead_of_treating_them_as_zero()
    {
        var service = Service(ListOf(Proc(1, "a", null), Proc(2, "b", 10), Proc(3, "c", 500)));

        var result = await service.ListAsync(new ProcessListOptions(ProcessSortKey.Memory));

        result.Select(p => p.ProcessId).Should().Equal(3, 2, 1);
    }

    [Fact]
    public async Task Name_filter_is_case_insensitive()
    {
        var result = await Service(ListOf(Proc(1, "Chrome"), Proc(2, "bash")))
            .ListAsync(new ProcessListOptions(NameFilter: "CHR"));

        result.Should().ContainSingle().Which.ProcessId.Should().Be(1);
    }

    [Fact]
    public async Task Cpu_sort_samples_twice_and_orders_by_measured_usage()
    {
        var before = new[] { Proc(1, cpuSeconds: 1), Proc(2, cpuSeconds: 1), Proc(3, cpuSeconds: 1) };
        var after = new[] { Proc(1, cpuSeconds: 1.005), Proc(2, cpuSeconds: 1.5), Proc(3, cpuSeconds: 1, start: Start.AddHours(1)) };
        var service = Service(new ScriptedProvider([before, after]));

        var result = await service.ListAsync(new ProcessListOptions(ProcessSortKey.Cpu, CpuSampleInterval: TimeSpan.FromMilliseconds(5)));

        result.Select(p => p.ProcessId).Should().Equal(2, 1, 3);
        result[0].CpuUsage.IsAvailable.Should().BeTrue();
        result[2].CpuUsage.IsAvailable.Should().BeFalse("PID 3 changed identity between samples");
    }

    [Fact]
    public async Task Cpu_sort_is_rejected_when_cpu_monitoring_is_not_supported()
    {
        var provider = ListOf(Proc(1));
        var service = Service(provider, [new CapabilityInfo(Capability.ProcessInspection, SupportStatus.Supported)]);

        var act = () => service.ListAsync(new ProcessListOptions(ProcessSortKey.Cpu));

        await act.Should().ThrowAsync<FeatureUnavailableException>();
    }

    [Fact]
    public async Task Plain_listing_leaves_cpu_usage_unavailable()
    {
        var result = await Service(ListOf(Proc(1))).ListAsync(new ProcessListOptions());

        result.Single().CpuUsage.IsAvailable.Should().BeFalse();
    }
}

public class ProcessInspectionServiceTests
{
    [Fact]
    public async Task Threads_and_modules_pass_through_the_provider_result_including_non_available_statuses()
    {
        var provider = new ScriptedProvider([[]])
        {
            Threads = InspectionResult<ThreadInfo>.Denied("nope"),
            Modules = InspectionResult<ModuleInfo>.NotFound(),
        };
        var service = new ProcessQueryService(provider, new CapabilityRegistry(PlatformKind.Linux, provider.GetCapabilities()));

        (await service.GetThreadsAsync(1)).Status.Should().Be(InspectionStatus.AccessDenied);
        (await service.GetModulesAsync(1)).Status.Should().Be(InspectionStatus.ProcessNotFound);
    }

    [Fact]
    public async Task Thread_and_module_inspection_are_rejected_when_the_capability_is_not_declared()
    {
        var provider = new ScriptedProvider([[]]);
        var service = new ProcessQueryService(provider, new CapabilityRegistry(PlatformKind.MacOS,
            [new CapabilityInfo(Capability.ModuleInspection, SupportStatus.NotSupported, "dyld not implemented")]));

        (await service.Invoking(s => s.GetThreadsAsync(1)).Should().ThrowAsync<FeatureUnavailableException>())
            .Which.Capability.Should().Be(Capability.ThreadInspection);
        (await service.Invoking(s => s.GetModulesAsync(1)).Should().ThrowAsync<FeatureUnavailableException>())
            .Which.Message.Should().Contain("dyld not implemented");
    }
}

public class ProcessMonitorTests
{
    private static ProcessMonitor Monitor(ScriptedProvider p) =>
        new(p, new CapabilityRegistry(PlatformKind.Linux, p.GetCapabilities()));

    private static async Task<List<MonitorSample>> Collect(ProcessMonitor m, int? max = null)
    {
        var list = new List<MonitorSample>();
        await foreach (var s in m.MonitorAsync(1, TimeSpan.FromMilliseconds(5), maxSamples: max)) list.Add(s);
        return list;
    }

    [Fact]
    public async Task Stops_after_max_samples()
    {
        var provider = new ScriptedProvider([[]],
            Enumerable.Range(0, 10).Select(i => (ProcessInfo?)Proc(1, cpuSeconds: i * 0.001)),
            Enumerable.Range(0, 10).Select(_ => (MemoryStats?)Mem(100)));

        var samples = await Collect(Monitor(provider), max: 3);

        samples.Should().HaveCount(3);
        samples[0].CpuPercentOfOneCore.IsAvailable.Should().BeFalse();
        samples[1].CpuPercentOfOneCore.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Stops_when_the_process_exits()
    {
        var provider = new ScriptedProvider([[]], [Proc(1), Proc(1), null], [Mem(1), Mem(1), Mem(1)]);

        (await Collect(Monitor(provider))).Should().HaveCount(2);
    }

    [Fact]
    public async Task Stops_when_the_pid_is_reused_by_a_different_process()
    {
        var provider = new ScriptedProvider([[]], [Proc(1), Proc(1, start: Start.AddMinutes(9)), Proc(1)], [Mem(1), Mem(1), Mem(1)]);

        (await Collect(Monitor(provider))).Should().HaveCount(1);
    }

    [Fact]
    public async Task Yields_nothing_for_a_process_that_does_not_exist() =>
        (await Collect(Monitor(new ScriptedProvider([[]])))).Should().BeEmpty();

    [Fact]
    public async Task Is_rejected_when_monitoring_is_not_supported()
    {
        var p = new ScriptedProvider([[]]);
        var monitor = new ProcessMonitor(p, new CapabilityRegistry(PlatformKind.Linux, []));

        var act = () => Collect(monitor);

        await act.Should().ThrowAsync<FeatureUnavailableException>();
    }
}
