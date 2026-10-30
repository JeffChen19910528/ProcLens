using ProcLens.Core.Dumps;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;

namespace ProcLens.Core.Reports;

/// <summary>
/// Why a section has, or lacks, data. "Not supported", "inaccessible" and "zero" are different facts and must never
/// be rendered alike.
/// </summary>
public enum SectionStatus
{
    Available,

    /// <summary>This platform or build cannot provide it.</summary>
    NotSupported,

    /// <summary>It could be provided, but the operating system refused access to this process.</summary>
    AccessDenied,

    ProcessNotFound,
    ArchitectureMismatch,

    /// <summary>It was not requested or not enough data was gathered (for example, one sample cannot give a CPU rate).</summary>
    NotCollected,

    Failed,
}

public sealed record Section<T>(SectionStatus Status, T? Data, string? Reason = null) where T : class
{
    public static Section<T> Ok(T data) => new(SectionStatus.Available, data);
    public static Section<T> Missing(SectionStatus status, string reason) => new(status, null, reason);
}

public sealed record ReportHeader(int SchemaVersion, DateTimeOffset GeneratedAt, string ApplicationVersion);

public sealed record SystemSection(
    string Platform,
    string OperatingSystem,
    string Architecture,
    int LogicalProcessors,
    string Runtime,
    string Machine,
    IReadOnlyList<CapabilityInfo> Capabilities);

/// <param name="Samples">Number of samples taken; a CPU rate needs at least two.</param>
public sealed record CpuSummary(
    int Samples,
    TimeSpan Interval,
    int LogicalProcessors,
    Measurement<TimeSpan> CpuTime,
    Measurement<double> AveragePercentOfMachine,
    Measurement<double> PeakPercentOfMachine,
    Measurement<double> AveragePercentOfOneCore);

public sealed record ThreadSummary(int Count, IReadOnlyDictionary<ThreadRunState, int> ByState, IReadOnlyList<ThreadInfo> Threads);

public sealed record CollectionError(string Section, SectionStatus Status, string Message);

public sealed record DiagnosticReport(
    ReportHeader Header,
    SystemSection System,
    Section<ProcessInfo> Process,
    Section<CpuSummary> Cpu,
    Section<MemoryStats> Memory,
    Section<ThreadSummary> Threads,
    Section<IReadOnlyList<ModuleInfo>> Modules,
    Section<IReadOnlyList<DumpMetadata>> Dumps,
    IReadOnlyList<DiagnosticWarning> Warnings,
    IReadOnlyList<CollectionError> Errors)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The fixed list of sections, in the order every renderer uses. Identical on every platform.</summary>
    public static IReadOnlyList<string> SectionNames { get; } =
        ["System", "Process", "Cpu", "Memory", "Threads", "Modules", "Dumps", "Warnings", "Errors"];
}
