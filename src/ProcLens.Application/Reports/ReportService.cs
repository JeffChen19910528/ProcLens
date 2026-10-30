using System.Reflection;
using System.Runtime.InteropServices;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Monitoring;
using ProcLens.Application.Processes;
using ProcLens.Core.Dumps;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Core.Reports;

namespace ProcLens.Application.Reports;

/// <param name="Samples">How many CPU/memory samples to take. A CPU rate needs at least two.</param>
/// <param name="DumpsDirectory">If given, dumps of this process recorded in that directory are listed (Windows only).</param>
public sealed record ReportOptions(
    int ProcessId,
    int Samples = 3,
    TimeSpan? Interval = null,
    string? DumpsDirectory = null,
    bool IncludeThreads = true,
    bool IncludeModules = true);

/// <summary>
/// Collects everything known about one process into a <see cref="DiagnosticReport"/>. Every section is always present;
/// when data is missing the section says why (not supported, access denied, not collected, ...) instead of staying
/// silent or showing zeros.
/// </summary>
public sealed class ReportService(
    IProcessQueryService queries,
    IProcessMonitor monitor,
    ICapabilityRegistry capabilities,
    TimeProvider? timeProvider = null) : IReportService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DiagnosticReport> CollectAsync(ReportOptions options, CancellationToken ct = default)
    {
        var header = new ReportHeader(DiagnosticReport.CurrentSchemaVersion, _time.GetUtcNow(), ApplicationVersion());
        var system = new SystemSection(
            capabilities.Platform.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription,
            Environment.MachineName,
            capabilities.All);

        var process = await CollectProcessAsync(options.ProcessId, ct);
        if (process.Data is not { } info)
        {
            // Nothing else can be said about a process that cannot be inspected; say so in every section.
            var reason = process.Reason ?? "The process could not be inspected.";
            return Assemble(header, system, process,
                Section<CpuSummary>.Missing(process.Status, reason),
                Section<MemoryStats>.Missing(process.Status, reason),
                Section<ThreadSummary>.Missing(process.Status, reason),
                Section<IReadOnlyList<ModuleInfo>>.Missing(process.Status, reason),
                Section<IReadOnlyList<DumpMetadata>>.Missing(process.Status, reason),
                []);
        }

        var (cpu, memory, sampleWarnings) = await CollectSamplesAsync(options, info, ct);
        var threads = options.IncludeThreads ? await CollectThreadsAsync(options.ProcessId, ct)
            : Section<ThreadSummary>.Missing(SectionStatus.NotCollected, "Not requested.");
        var modules = options.IncludeModules ? await CollectModulesAsync(options.ProcessId, ct)
            : Section<IReadOnlyList<ModuleInfo>>.Missing(SectionStatus.NotCollected, "Not requested.");
        var dumps = CollectDumps(options.DumpsDirectory, info);

        return Assemble(header, system, process, cpu, memory, threads, modules, dumps, sampleWarnings);
    }

    private static DiagnosticReport Assemble(
        ReportHeader header, SystemSection system, Section<ProcessInfo> process, Section<CpuSummary> cpu, Section<MemoryStats> memory,
        Section<ThreadSummary> threads, Section<IReadOnlyList<ModuleInfo>> modules, Section<IReadOnlyList<DumpMetadata>> dumps,
        IReadOnlyList<DiagnosticWarning> sampleWarnings)
    {
        var errors = ReportAnalyzer.Errors(
        [
            ("Process", process.Status, process.Reason),
            ("Cpu", cpu.Status, cpu.Reason),
            ("Memory", memory.Status, memory.Reason),
            ("Threads", threads.Status, threads.Reason),
            ("Modules", modules.Status, modules.Reason),
            ("Dumps", dumps.Status, dumps.Reason),
        ]);

        return new DiagnosticReport(header, system, process, cpu, memory, threads, modules, dumps,
            ReportAnalyzer.Warnings(process, cpu, threads, dumps, sampleWarnings), errors);
    }

    // ---------- process ----------

    private async Task<Section<ProcessInfo>> CollectProcessAsync(int processId, CancellationToken ct)
    {
        try
        {
            return await queries.InspectAsync(processId, ct) is { } info
                ? Section<ProcessInfo>.Ok(info)
                : Section<ProcessInfo>.Missing(SectionStatus.ProcessNotFound, $"Process {processId} was not found or has exited.");
        }
        catch (FeatureUnavailableException e)
        {
            return Section<ProcessInfo>.Missing(SectionStatus.NotSupported, e.Message);
        }
    }

    // ---------- CPU and memory ----------

    private async Task<(Section<CpuSummary> Cpu, Section<MemoryStats> Memory, IReadOnlyList<DiagnosticWarning> Warnings)> CollectSamplesAsync(
        ReportOptions options, ProcessInfo info, CancellationToken ct)
    {
        var interval = options.Interval ?? DefaultInterval;
        var wanted = Math.Max(1, options.Samples);
        var cpuReason = ReasonFor(Capability.CpuMonitoring);
        var memoryReason = ReasonFor(Capability.MemoryMonitoring);

        // Sampling needs both capabilities; when only memory exists it is read once, and CPU is reported as unsupported.
        if (cpuReason is not null || memoryReason is not null)
        {
            var cpu = Section<CpuSummary>.Missing(SectionStatus.NotSupported, cpuReason ?? "CPU sampling is unavailable because memory sampling is not supported here.");
            Section<MemoryStats> memory;
            if (memoryReason is not null)
            {
                memory = Section<MemoryStats>.Missing(SectionStatus.NotSupported, memoryReason);
            }
            else
            {
                var stats = await queries.GetMemoryAsync(options.ProcessId, ct);
                memory = stats is null
                    ? Section<MemoryStats>.Missing(SectionStatus.ProcessNotFound, "The process exited before memory could be read.")
                    : Section<MemoryStats>.Ok(stats);
            }
            return (cpu, memory, []);
        }

        var samples = new List<MonitorSample>();
        await foreach (var sample in monitor.MonitorAsync(options.ProcessId, interval, maxSamples: wanted, ct: ct))
            samples.Add(sample);

        if (samples.Count == 0)
        {
            return (Section<CpuSummary>.Missing(SectionStatus.ProcessNotFound, "The process exited before it could be sampled."),
                    Section<MemoryStats>.Missing(SectionStatus.ProcessNotFound, "The process exited before it could be sampled."), []);
        }

        var warnings = samples.SelectMany(s => s.Warnings).ToList();
        if (samples.Count < wanted)
            warnings.Add(new("PROCESS_EXITED", $"The process ended after {samples.Count} of {wanted} samples; figures cover only the time it was alive."));

        return (SummariseCpu(samples, interval, info), Section<MemoryStats>.Ok(samples[^1].Memory), warnings);
    }

    private static Section<CpuSummary> SummariseCpu(List<MonitorSample> samples, TimeSpan interval, ProcessInfo info)
    {
        if (samples.Count < 2)
            return Section<CpuSummary>.Missing(SectionStatus.NotCollected, "A CPU rate needs at least two samples (use --samples 2 or more).");

        var machine = samples.Select(s => s.Process.CpuUsage).Where(m => m.IsAvailable).Select(m => m.Value!.Value).ToList();
        var oneCore = samples.Select(s => s.CpuPercentOfOneCore).Where(m => m.IsAvailable).Select(m => m.Value!.Value).ToList();
        if (machine.Count == 0)
            return Section<CpuSummary>.Missing(SectionStatus.AccessDenied,
                "CPU time could not be read for this process (access denied), or its identity changed while sampling.");

        return Section<CpuSummary>.Ok(new CpuSummary(
            samples.Count, interval, Environment.ProcessorCount, samples[^1].Process.CpuTime,
            Measurement<double>.Of(machine.Average()),
            Measurement<double>.Of(machine.Max()),
            Measurement<double>.Of(oneCore.Average())));
    }

    private string? ReasonFor(Capability capability) =>
        capabilities.IsSupported(capability)
            ? null
            : capabilities.All.First(c => c.Capability == capability).Reason ?? "Not supported on this platform.";

    // ---------- threads, modules ----------

    private async Task<Section<ThreadSummary>> CollectThreadsAsync(int processId, CancellationToken ct)
    {
        if (ReasonFor(Capability.ThreadInspection) is { } unsupported)
            return Section<ThreadSummary>.Missing(SectionStatus.NotSupported, unsupported);

        var result = await queries.GetThreadsAsync(processId, ct);
        if (result.Status != InspectionStatus.Available)
            return Section<ThreadSummary>.Missing(ReportAnalyzer.FromInspection(result.Status), result.Reason ?? result.Status.ToString());

        var byState = result.Items.GroupBy(t => t.State).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
        return Section<ThreadSummary>.Ok(new ThreadSummary(result.Items.Count, byState, result.Items));
    }

    private async Task<Section<IReadOnlyList<ModuleInfo>>> CollectModulesAsync(int processId, CancellationToken ct)
    {
        if (ReasonFor(Capability.ModuleInspection) is { } unsupported)
            return Section<IReadOnlyList<ModuleInfo>>.Missing(SectionStatus.NotSupported, unsupported);

        var result = await queries.GetModulesAsync(processId, ct);
        return result.Status == InspectionStatus.Available
            ? Section<IReadOnlyList<ModuleInfo>>.Ok(result.Items)
            : Section<IReadOnlyList<ModuleInfo>>.Missing(ReportAnalyzer.FromInspection(result.Status), result.Reason ?? result.Status.ToString());
    }

    // ---------- dumps ----------

    private Section<IReadOnlyList<DumpMetadata>> CollectDumps(string? directory, ProcessInfo info)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.NotCollected, "No dump directory was given (use --dumps-dir).");
        if (ReasonFor(Capability.ProcessDump) is { } unsupported)
            return Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.NotSupported, unsupported);
        if (!Directory.Exists(directory))
            return Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.Failed, $"Dump directory not found: {directory}");

        var found = new List<DumpMetadata>();
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(directory, "*" + DumpFiles.SidecarSuffix))
            {
                var name = Path.GetFileName(sidecar);
                if (!name.EndsWith(".dmp.json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dmp.enc.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (DumpFiles.TryReadMetadata(sidecar[..^DumpFiles.SidecarSuffix.Length]) is not { } metadata) continue;
                if (metadata.ProcessId != info.ProcessId) continue;
                // A dump of an earlier process that happened to have the same id must not be attributed to this one.
                if (info.StartTime is { } start && Math.Abs((metadata.ProcessStartTime - start).TotalSeconds) > 2) continue;
                found.Add(metadata);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.AccessDenied, $"The dump directory could not be read: {e.Message}");
        }

        return Section<IReadOnlyList<DumpMetadata>>.Ok(found.OrderBy(d => d.CreationTime).ToList());
    }

    private static string ApplicationVersion() =>
        (typeof(ReportService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown").Split('+')[0];
}
