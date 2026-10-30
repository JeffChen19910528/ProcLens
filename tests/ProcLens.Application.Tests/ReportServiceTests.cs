using FluentAssertions;
using ProcLens.Application.Monitoring;
using ProcLens.Application.Processes;
using ProcLens.Application.Reports;
using ProcLens.Application.Tests.Support;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Core.Reports;
using static ProcLens.Application.Tests.Support.Builders;

namespace ProcLens.Application.Tests;

public class ReportServiceTests : IDisposable
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-rep-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static IEnumerable<CapabilityInfo> AllSupported() =>
        Enum.GetValues<Capability>().Select(c => new CapabilityInfo(c, SupportStatus.Supported));

    private static IEnumerable<CapabilityInfo> Except(Capability missing, string reason) =>
        Enum.GetValues<Capability>().Select(c => c == missing
            ? new CapabilityInfo(c, SupportStatus.NotSupported, reason)
            : new CapabilityInfo(c, SupportStatus.Supported));

    /// <summary>Provider answering "inspect", then <paramref name="samples"/> process/memory readings.</summary>
    private static ScriptedProvider Provider(
        int samples = 3, InspectionResult<ThreadInfo>? threads = null, InspectionResult<ModuleInfo>? modules = null,
        double cpuStepSeconds = 1, bool cpuReadable = true, ProcessInfo? first = null)
    {
        var infos = new List<ProcessInfo?> { first ?? Proc(7, "demo", start: Start, cpuSeconds: 0) };
        var memory = new List<MemoryStats?>();
        for (var i = 0; i < samples; i++)
        {
            infos.Add(Proc(7, "demo", start: Start, cpuSeconds: cpuReadable ? i * cpuStepSeconds : null));
            memory.Add(Mem(1000 + i));
        }

        return new ScriptedProvider([[]], infos, memory)
        {
            Threads = threads ?? InspectionResult<ThreadInfo>.Ok(
            [
                new ThreadInfo(1, "main", ThreadRunState.Waiting, null, Measurement<TimeSpan>.Of(TimeSpan.Zero)),
                new ThreadInfo(2, null, ThreadRunState.Running, null, Measurement<TimeSpan>.Unavailable("denied")),
                new ThreadInfo(3, null, ThreadRunState.Waiting, null, Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(1))),
            ]),
            Modules = modules ?? InspectionResult<ModuleInfo>.Ok(
                [new ModuleInfo("a.dll", "/x/a.dll", Measurement<ulong>.Of(0x1000), Measurement<long>.Of(4096))]),
        };
    }

    private static ReportService Service(ScriptedProvider provider, IEnumerable<CapabilityInfo>? caps = null, PlatformKind platform = PlatformKind.Windows)
    {
        var registry = new CapabilityRegistry(platform, caps ?? AllSupported());
        return new ReportService(new ProcessQueryService(provider, registry), new ProcessMonitor(provider, registry), registry);
    }

    private static ReportOptions Options(int samples = 3, string? dumps = null, bool threads = true, bool modules = true) =>
        new(7, samples, Quick, dumps, threads, modules);

    // ---------- the complete case ----------

    [Fact]
    public async Task A_full_report_has_every_section_available_with_measured_cpu_and_the_last_memory_reading()
    {
        var report = await Service(Provider()).CollectAsync(Options());

        report.Process.Status.Should().Be(SectionStatus.Available);
        report.Cpu.Status.Should().Be(SectionStatus.Available);
        report.Cpu.Data!.Samples.Should().Be(3);
        report.Cpu.Data.AveragePercentOfMachine.IsAvailable.Should().BeTrue();
        report.Cpu.Data.AveragePercentOfMachine.Value.Should().BeGreaterThan(0);
        report.Cpu.Data.PeakPercentOfMachine.Value.Should().BeGreaterThanOrEqualTo(report.Cpu.Data.AveragePercentOfMachine.Value!.Value);
        report.Memory.Data!.WorkingSet.Value.Should().Be(1002, "the last sample is reported");
        report.Threads.Data!.Count.Should().Be(3);
        report.Threads.Data.ByState.Should().Contain([KeyValuePair.Create(ThreadRunState.Waiting, 2), KeyValuePair.Create(ThreadRunState.Running, 1)]);
        report.Modules.Data.Should().ContainSingle();
        report.System.Platform.Should().Be("Windows");
        report.System.Capabilities.Should().HaveCount(Enum.GetValues<Capability>().Length);
        report.Header.SchemaVersion.Should().Be(DiagnosticReport.CurrentSchemaVersion);
        report.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Sections_not_asked_for_are_not_collected_and_are_not_errors()
    {
        var report = await Service(Provider()).CollectAsync(Options(threads: false, modules: false));

        report.Threads.Status.Should().Be(SectionStatus.NotCollected);
        report.Modules.Status.Should().Be(SectionStatus.NotCollected);
        report.Dumps.Status.Should().Be(SectionStatus.NotCollected);
        report.Errors.Should().BeEmpty();
    }

    // ---------- not supported vs inaccessible vs not collected ----------

    [Fact]
    public async Task Unsupported_cpu_is_reported_as_not_supported_with_the_platforms_reason_while_memory_still_works()
    {
        var provider = new ScriptedProvider([[]], [Proc(7, start: Start)], [Mem(500)]);
        var report = await Service(provider, Except(Capability.CpuMonitoring, "no sampler on this OS")).CollectAsync(Options());

        report.Cpu.Should().Match<Section<CpuSummary>>(s => s.Status == SectionStatus.NotSupported && s.Reason == "no sampler on this OS");
        report.Memory.Status.Should().Be(SectionStatus.Available);
        report.Memory.Data!.WorkingSet.Value.Should().Be(500);
        report.Errors.Should().BeEmpty("a known platform limitation is not a collection error");
    }

    [Fact]
    public async Task Unsupported_modules_and_threads_are_not_supported_not_empty()
    {
        var caps = Except(Capability.ModuleInspection, "no dyld enumeration").Select(c =>
            c.Capability == Capability.ThreadInspection ? c with { Status = SupportStatus.NotSupported, Reason = "no threads" } : c);

        var report = await Service(Provider(), caps, PlatformKind.MacOS).CollectAsync(Options());

        report.Modules.Should().Match<Section<IReadOnlyList<ModuleInfo>>>(s => s.Status == SectionStatus.NotSupported && s.Data == null);
        report.Threads.Status.Should().Be(SectionStatus.NotSupported);
        report.System.Platform.Should().Be("MacOS");
    }

    [Fact]
    public async Task Access_denied_and_architecture_mismatch_are_reported_as_errors_with_their_own_status()
    {
        var provider = Provider(threads: InspectionResult<ThreadInfo>.Denied("threads hidden"),
            modules: InspectionResult<ModuleInfo>.ArchitectureMismatch("32-bit inspector, 64-bit target"));

        var report = await Service(provider).CollectAsync(Options());

        report.Threads.Should().Match<Section<ThreadSummary>>(s => s.Status == SectionStatus.AccessDenied && s.Reason == "threads hidden");
        report.Modules.Status.Should().Be(SectionStatus.ArchitectureMismatch);
        report.Errors.Select(e => (e.Section, e.Status)).Should().Equal(("Threads", SectionStatus.AccessDenied), ("Modules", SectionStatus.ArchitectureMismatch));
    }

    [Fact]
    public async Task A_single_sample_cannot_give_a_cpu_rate_and_says_so()
    {
        var report = await Service(Provider(samples: 1)).CollectAsync(Options(samples: 1));

        report.Cpu.Status.Should().Be(SectionStatus.NotCollected);
        report.Cpu.Reason.Should().Contain("at least two samples");
        report.Memory.Status.Should().Be(SectionStatus.Available);
        report.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreadable_cpu_time_is_access_denied_not_zero_percent()
    {
        var report = await Service(Provider(cpuReadable: false, first: Proc(7, "demo", start: Start, cpuSeconds: null))).CollectAsync(Options());

        report.Cpu.Status.Should().Be(SectionStatus.AccessDenied);
        report.Cpu.Data.Should().BeNull();
        report.Errors.Should().Contain(e => e.Section == "Cpu");
    }

    [Fact]
    public async Task A_genuinely_idle_process_is_a_measured_zero_not_unavailable()
    {
        var report = await Service(Provider(cpuStepSeconds: 0)).CollectAsync(Options());

        report.Cpu.Status.Should().Be(SectionStatus.Available);
        report.Cpu.Data!.AveragePercentOfMachine.IsAvailable.Should().BeTrue();
        report.Cpu.Data.AveragePercentOfMachine.Value.Should().Be(0);
    }

    // ---------- process gone ----------

    [Fact]
    public async Task A_missing_process_yields_a_report_where_every_section_says_so()
    {
        var report = await Service(new ScriptedProvider([[]])).CollectAsync(Options());

        new[] { report.Process.Status, report.Cpu.Status, report.Memory.Status, report.Threads.Status, report.Modules.Status, report.Dumps.Status }
            .Should().OnlyContain(s => s == SectionStatus.ProcessNotFound);
        report.Errors.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_process_that_exits_while_being_sampled_keeps_what_was_measured_and_warns()
    {
        var provider = new ScriptedProvider([[]],
            [Proc(7, start: Start, cpuSeconds: 0), Proc(7, start: Start, cpuSeconds: 0), Proc(7, start: Start, cpuSeconds: 1), null],
            [Mem(1), Mem(2)])
        { Threads = InspectionResult<ThreadInfo>.NotFound(), Modules = InspectionResult<ModuleInfo>.NotFound() };

        var report = await Service(provider).CollectAsync(Options(samples: 5));

        report.Cpu.Data!.Samples.Should().Be(2);
        report.Warnings.Should().Contain(w => w.Code == "PROCESS_EXITED" && w.Message.Contains("2 of 5"));
    }

    [Fact]
    public async Task A_process_that_vanishes_before_sampling_reports_it_in_cpu_and_memory()
    {
        var provider = new ScriptedProvider([[]], [Proc(7, start: Start)]) // inspected once, then gone
        { Threads = InspectionResult<ThreadInfo>.NotFound(), Modules = InspectionResult<ModuleInfo>.NotFound() };

        var report = await Service(provider).CollectAsync(Options());

        report.Cpu.Status.Should().Be(SectionStatus.ProcessNotFound);
        report.Memory.Status.Should().Be(SectionStatus.ProcessNotFound);
    }

    // ---------- warnings ----------

    [Fact]
    public async Task Limited_access_and_memory_growth_become_warnings()
    {
        var provider = new ScriptedProvider([[]],
            [Proc(7, start: Start, cpuSeconds: 0) with { AccessStatus = AccessStatus.Partial },
             Proc(7, start: Start, cpuSeconds: 0), Proc(7, start: Start, cpuSeconds: 0), Proc(7, start: Start, cpuSeconds: 0),
             Proc(7, start: Start, cpuSeconds: 0), Proc(7, start: Start, cpuSeconds: 0)],
            [Mem(1000), Mem(1100), Mem(1250), Mem(1500), Mem(2000)])
        { Threads = InspectionResult<ThreadInfo>.Ok([]), Modules = InspectionResult<ModuleInfo>.Ok([]) };

        var report = await Service(provider).CollectAsync(Options(samples: 5));

        report.Warnings.Select(w => w.Code).Should().Contain(["LIMITED_ACCESS", "MEMORY_GROWTH"]);
    }

    // ---------- dumps ----------

    private string WriteDump(int pid, DateTimeOffset started, string? scheme, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1]);
        File.WriteAllText(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(new DumpMetadata(
            Guid.NewGuid(), pid, "demo", started, DumpType.Heap, path, 1, DateTimeOffset.UtcNow, "OS", "1", CollectionStatus.Succeeded, null, "a", "b", scheme)));
        return path;
    }

    [Fact]
    public async Task Dumps_are_listed_only_for_the_same_process_instance()
    {
        WriteDump(7, Start, "scheme", "mine.dmp.enc");
        WriteDump(8, Start, "scheme", "other-pid.dmp.enc");
        WriteDump(7, Start.AddHours(5), "scheme", "reused-pid.dmp.enc");
        File.WriteAllText(Path.Combine(_dir, "junk.dmp.json"), "{ nope");

        var report = await Service(Provider()).CollectAsync(Options(dumps: _dir));

        report.Dumps.Status.Should().Be(SectionStatus.Available);
        report.Dumps.Data.Should().ContainSingle().Which.DumpFilePath.Should().EndWith("mine.dmp.enc");
        report.Warnings.Should().NotContain(w => w.Code == "UNENCRYPTED_DUMP");
    }

    [Fact]
    public async Task An_unencrypted_dump_of_the_process_is_flagged()
    {
        WriteDump(7, Start, null, "plain.dmp");

        var report = await Service(Provider()).CollectAsync(Options(dumps: _dir));

        report.Warnings.Should().ContainSingle(w => w.Code == "UNENCRYPTED_DUMP");
    }

    [Fact]
    public async Task No_dumps_found_is_an_empty_available_section_distinct_from_not_collected()
    {
        var report = await Service(Provider()).CollectAsync(Options(dumps: _dir));

        report.Dumps.Status.Should().Be(SectionStatus.Available);
        report.Dumps.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Dumps_are_not_supported_where_the_platform_has_no_dump_capability_and_a_missing_directory_is_a_failure()
    {
        var unsupported = await Service(Provider(), Except(Capability.ProcessDump, "Windows only"), PlatformKind.Linux).CollectAsync(Options(dumps: _dir));
        var missing = await Service(Provider()).CollectAsync(Options(dumps: Path.Combine(_dir, "nope")));

        unsupported.Dumps.Should().Match<Section<IReadOnlyList<DumpMetadata>>>(s => s.Status == SectionStatus.NotSupported && s.Reason == "Windows only");
        unsupported.Errors.Should().BeEmpty();
        missing.Dumps.Status.Should().Be(SectionStatus.Failed);
        missing.Errors.Should().ContainSingle(e => e.Section == "Dumps");
    }
}
