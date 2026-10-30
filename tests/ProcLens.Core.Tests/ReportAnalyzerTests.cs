using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Core.Reports;

namespace ProcLens.Core.Tests;

public class ReportAnalyzerTests
{
    private static ProcessInfo Process(AccessStatus access = AccessStatus.Full) => new(
        1, "p", null, null, null, null, Measurement<double>.Unavailable("x"), Measurement<TimeSpan>.Unavailable("x"),
        Measurement<long>.Unavailable("x"), Measurement<int>.Unavailable("x"), PlatformKind.Windows, access);

    private static Section<CpuSummary> Cpu(double? machineAverage) => machineAverage is { } v
        ? Section<CpuSummary>.Ok(new CpuSummary(3, TimeSpan.FromSeconds(1), 8, Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(1)),
            Measurement<double>.Of(v), Measurement<double>.Of(v), Measurement<double>.Of(v * 8)))
        : Section<CpuSummary>.Missing(SectionStatus.NotSupported, "n/a");

    private static IReadOnlyList<DiagnosticWarning> Warn(
        ProcessInfo? process = null, Section<CpuSummary>? cpu = null, Section<ThreadSummary>? threads = null,
        Section<IReadOnlyList<DumpMetadata>>? dumps = null, params DiagnosticWarning[] sample) =>
        ReportAnalyzer.Warnings(
            process is null ? Section<ProcessInfo>.Missing(SectionStatus.NotCollected, "-") : Section<ProcessInfo>.Ok(process),
            cpu ?? Cpu(null),
            threads ?? Section<ThreadSummary>.Missing(SectionStatus.NotCollected, "-"),
            dumps ?? Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.NotCollected, "-"),
            sample);

    private static Section<IReadOnlyList<DumpMetadata>> Dumps(string? scheme) => Section<IReadOnlyList<DumpMetadata>>.Ok(
    [
        new DumpMetadata(Guid.NewGuid(), 1, "p", DateTimeOffset.UtcNow, DumpType.Mini, @"C:\d\x.dmp", 1, DateTimeOffset.UtcNow, "OS", "1",
            CollectionStatus.Succeeded, null, "a", "b", scheme),
    ]);

    [Theory]
    [InlineData(AccessStatus.Partial, "partial")]
    [InlineData(AccessStatus.Denied, "minimal")]
    public void Limited_access_to_the_process_is_called_out(AccessStatus access, string wording) =>
        Warn(Process(access)).Should().ContainSingle(w => w.Code == "LIMITED_ACCESS" && w.Message.Contains(wording));

    [Fact]
    public void Full_access_raises_no_warning() => Warn(Process()).Should().BeEmpty();

    [Theory]
    [InlineData(79.9, false)]
    [InlineData(80.0, true)]
    [InlineData(100.0, true)]
    [InlineData(0.0, false)]
    public void High_cpu_is_flagged_from_the_average_at_the_threshold(double average, bool flagged) =>
        Warn(cpu: Cpu(average)).Any(w => w.Code == "HIGH_CPU").Should().Be(flagged);

    [Fact]
    public void An_unmeasured_cpu_never_raises_a_cpu_warning() => Warn(cpu: Cpu(null)).Should().BeEmpty();

    [Fact]
    public void Warnings_found_while_sampling_are_reported_once_each()
    {
        var growth = new DiagnosticWarning("MEMORY_GROWTH", "grew");

        var warnings = Warn(sample: [growth, growth, growth, new DiagnosticWarning("HIGH_CPU", "instant")]);

        warnings.Select(w => w.Code).Should().Equal("MEMORY_GROWTH", "HIGH_CPU");
    }

    [Fact]
    public void The_summary_cpu_warning_is_not_duplicated_by_a_sampling_one()
    {
        var warnings = Warn(cpu: Cpu(95), sample: new DiagnosticWarning("HIGH_CPU", "instant"));

        warnings.Count(w => w.Code == "HIGH_CPU").Should().Be(1);
    }

    [Fact]
    public void Unencrypted_dumps_are_a_warning_encrypted_ones_are_not()
    {
        Warn(dumps: Dumps(null)).Should().ContainSingle(w => w.Code == "UNENCRYPTED_DUMP" && w.Message.Contains("x.dmp"));
        Warn(dumps: Dumps("scheme")).Should().BeEmpty();
    }

    [Fact]
    public void A_platform_that_reports_no_thread_states_gets_one_explanatory_warning()
    {
        var unknown = new ThreadSummary(3, new Dictionary<ThreadRunState, int> { [ThreadRunState.Unknown] = 3 }, []);
        var known = new ThreadSummary(3, new Dictionary<ThreadRunState, int> { [ThreadRunState.Waiting] = 3 }, []);

        Warn(threads: Section<ThreadSummary>.Ok(unknown)).Should().ContainSingle(w => w.Code == "THREAD_STATE_UNKNOWN");
        Warn(threads: Section<ThreadSummary>.Ok(known)).Should().BeEmpty();
    }

    [Fact]
    public void Errors_include_failures_but_not_sections_that_were_unsupported_or_not_requested()
    {
        var errors = ReportAnalyzer.Errors(
        [
            ("Process", SectionStatus.Available, null),
            ("Cpu", SectionStatus.NotSupported, "no"),
            ("Memory", SectionStatus.NotCollected, "not asked"),
            ("Threads", SectionStatus.AccessDenied, "denied"),
            ("Modules", SectionStatus.ArchitectureMismatch, "32 vs 64"),
            ("Dumps", SectionStatus.Failed, null),
        ]);

        errors.Select(e => e.Section).Should().Equal("Threads", "Modules", "Dumps");
        errors[2].Message.Should().Be("Failed", "a missing reason falls back to the status name");
    }

    [Theory]
    [InlineData(InspectionStatus.Available, SectionStatus.Available)]
    [InlineData(InspectionStatus.NotSupported, SectionStatus.NotSupported)]
    [InlineData(InspectionStatus.AccessDenied, SectionStatus.AccessDenied)]
    [InlineData(InspectionStatus.ProcessNotFound, SectionStatus.ProcessNotFound)]
    [InlineData(InspectionStatus.ArchitectureMismatch, SectionStatus.ArchitectureMismatch)]
    public void Inspection_statuses_map_one_to_one(InspectionStatus input, SectionStatus expected) =>
        ReportAnalyzer.FromInspection(input).Should().Be(expected);

    [Fact]
    public void Every_section_status_is_distinct_so_they_can_never_be_confused()
    {
        Enum.GetValues<SectionStatus>().Should().OnlyHaveUniqueItems();
        DiagnosticReport.SectionNames.Should().Equal("System", "Process", "Cpu", "Memory", "Threads", "Modules", "Dumps", "Warnings", "Errors");
    }
}
