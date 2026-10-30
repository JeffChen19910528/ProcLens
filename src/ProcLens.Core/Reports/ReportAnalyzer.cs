using ProcLens.Core.Dumps;
using ProcLens.Core.Models;

namespace ProcLens.Core.Reports;

/// <summary>Turns collected sections into warnings and collection errors. Pure, so each rule is unit tested.</summary>
public static class ReportAnalyzer
{
    public const double HighCpuPercentOfMachine = 80;

    public static IReadOnlyList<DiagnosticWarning> Warnings(
        Section<ProcessInfo> process,
        Section<CpuSummary> cpu,
        Section<ThreadSummary> threads,
        Section<IReadOnlyList<DumpMetadata>> dumps,
        IEnumerable<DiagnosticWarning> sampleWarnings)
    {
        var warnings = new List<DiagnosticWarning>();

        if (process.Data is { AccessStatus: not AccessStatus.Full } info)
            warnings.Add(new("LIMITED_ACCESS",
                $"Only {(info.AccessStatus == AccessStatus.Denied ? "minimal" : "partial")} information could be read about this process; values marked n/a were not readable."));

        if (cpu.Data is { AveragePercentOfMachine.Value: { } average } && average >= HighCpuPercentOfMachine)
            warnings.Add(new("HIGH_CPU", $"Average CPU usage was {average:F1}% of the machine over the sampling period."));

        // Rules evaluated while sampling (growth, instantaneous CPU) are reported once each, not once per sample.
        foreach (var warning in sampleWarnings.GroupBy(w => w.Code).Select(g => g.First()))
            if (warnings.All(w => w.Code != warning.Code))
                warnings.Add(warning);

        if (threads.Data is { } summary && summary.ByState.GetValueOrDefault(ThreadRunState.Unknown) == summary.Count && summary.Count > 0)
            warnings.Add(new("THREAD_STATE_UNKNOWN", "This platform does not report thread states; the per-state counts are not informative."));

        foreach (var dump in dumps.Data ?? [])
            if (dump.EncryptionScheme is null)
                warnings.Add(new("UNENCRYPTED_DUMP",
                    $"A dump of this process exists unencrypted: {Path.GetFileName(dump.DumpFilePath)}. It may contain secrets from the process's memory."));

        return warnings;
    }

    /// <summary>
    /// Sections that could have had data but failed to deliver it. Not an error: a section that was not requested, and
    /// one the platform does not support at all (that is a known limitation, already shown in the section and in the
    /// capability table).
    /// </summary>
    public static IReadOnlyList<CollectionError> Errors(IEnumerable<(string Name, SectionStatus Status, string? Reason)> sections) =>
        sections
            .Where(s => s.Status is not (SectionStatus.Available or SectionStatus.NotCollected or SectionStatus.NotSupported))
            .Select(s => new CollectionError(s.Name, s.Status, s.Reason ?? s.Status.ToString()))
            .ToList();

    public static SectionStatus FromInspection(InspectionStatus status) => status switch
    {
        InspectionStatus.Available => SectionStatus.Available,
        InspectionStatus.NotSupported => SectionStatus.NotSupported,
        InspectionStatus.AccessDenied => SectionStatus.AccessDenied,
        InspectionStatus.ProcessNotFound => SectionStatus.ProcessNotFound,
        InspectionStatus.ArchitectureMismatch => SectionStatus.ArchitectureMismatch,
        _ => SectionStatus.Failed,
    };
}
