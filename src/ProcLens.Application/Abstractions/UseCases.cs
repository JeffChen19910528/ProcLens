using ProcLens.Application.Dumps;
using ProcLens.Application.Monitoring;
using ProcLens.Application.Processes;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Models;
using ProcLens.Core.Reports;

namespace ProcLens.Application.Abstractions;

// The use cases the command layer (and other use cases) depend on. Depending on these instead of the concrete
// classes keeps the callers independent of how a use case is built, and lets any of them be replaced in a test.

public interface IProcessQueryService
{
    Task<IReadOnlyList<ProcessInfo>> ListAsync(ProcessListOptions options, CancellationToken ct = default);
    Task<ProcessInfo?> InspectAsync(int processId, CancellationToken ct = default);
    Task<MemoryStats?> GetMemoryAsync(int processId, CancellationToken ct = default);
    Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default);
    Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default);
}

public interface IProcessMonitor
{
    IAsyncEnumerable<MonitorSample> MonitorAsync(
        int processId, TimeSpan interval, MonitorThresholds? thresholds = null, int? maxSamples = null, CancellationToken ct = default);
}

public interface IDumpService
{
    bool IsSupported { get; }
    Task<DumpResult> CreateAsync(DumpCreateOptions options, CancellationToken ct = default);
}

public interface IDumpVaultService
{
    Task<VaultResult> DecryptAsync(string file, string outputFile, SecretText? password, CancellationToken ct = default);
    Task<VaultResult> ExportAsync(string file, string outputFile, SecretText newPassword, SecretText? currentPassword, CancellationToken ct = default);
    Task<VaultResult> VerifyAsync(string file, SecretText? password, CancellationToken ct = default);
}

public interface IDumpRetentionService
{
    Task<RetentionReport> RunAsync(string directory, RetentionPolicy policy, bool delete, CancellationToken ct = default);
}

public interface IReportService
{
    Task<DiagnosticReport> CollectAsync(Reports.ReportOptions options, CancellationToken ct = default);
}
