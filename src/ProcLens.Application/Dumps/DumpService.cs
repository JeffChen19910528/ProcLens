using ProcLens.Application.Abstractions;
using ProcLens.Application.Audit;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Application.Dumps;

/// <param name="Encrypt">Protect the dump with the machine/user key. On by default; callers must opt out explicitly.</param>
/// <param name="Password">Also allow opening the dump with this password (portable).</param>
/// <param name="Admin">Explicit administrative authorization for processes owned by another user.</param>
public sealed record DumpCreateOptions(
    int ProcessId,
    DumpType Type,
    string OutputDirectory,
    bool Encrypt = true,
    SecretText? Password = null,
    AdminAuthorization? Admin = null,
    IProgress<DumpProgress>? Progress = null);

public sealed class DumpService(
    IPlatformProcessProvider processes,
    IProcessDumpProvider dumps,
    IAuditLog audit,
    IOperatorContext operatorContext,
    TimeProvider? timeProvider = null) : IDumpService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly AuditRecorder _recorder = new(audit, operatorContext, timeProvider);

    public bool IsSupported => dumps.IsSupported;

    public async Task<DumpResult> CreateAsync(DumpCreateOptions options, CancellationToken ct = default)
    {
        // Unsupported platforms answer immediately with their explicit PlatformNotSupported result;
        // no operation is attempted, so nothing is audited and nothing about the target is looked up.
        if (!dumps.IsSupported)
            return await dumps.CreateDumpAsync(new DumpRequest(options.ProcessId, options.Type, options.OutputDirectory), ct);

        // Privileged operations are accountable: if the audit trail cannot be written, they do not run.
        if (await _recorder.CheckAvailableAsync(ct) is { } auditProblem)
            return new DumpResult(DumpStatus.Failed, Message: auditProblem);

        var info = await processes.GetProcessInfoAsync(options.ProcessId, ct);
        if (info is null)
            return await RecordAsync(options, null, new DumpResult(DumpStatus.ProcessNotFound, Message: $"Process {options.ProcessId} was not found."), ct);
        if (info.StartTime is null)
            return await RecordAsync(options, info,
                new DumpResult(DumpStatus.AccessDenied, Message: "The process start time cannot be read, so its identity cannot be verified and no dump was taken."), ct);

        // The identity (pid + start time) observed here is what the provider later verifies against its own
        // process handle, which is what catches a PID reused in between.
        var result = await dumps.CreateDumpAsync(
            new DumpRequest(options.ProcessId, options.Type, options.OutputDirectory, info.StartTime, info.ProcessName,
                options.Progress, options.Encrypt, options.Password, options.Admin), ct);

        return await RecordAsync(options, info, result, ct);
    }

    private async Task<DumpResult> RecordAsync(
        DumpCreateOptions options, Core.Models.ProcessInfo? info, DumpResult result, CancellationToken ct)
    {
        var authorization = result.Authorization is { } a
            ? new AuditAuthorization(a.Allowed, string.IsNullOrEmpty(a.Policy) ? "n/a" : a.Policy, AuditRecord.Clean(a.Reason) ?? "")
            : new AuditAuthorization(false, "n/a", "Rejected before authorization was evaluated.");

        var record = _recorder.NewRecord("dump.create", authorization, OutcomeOf(result.Status)) with
        {
            TargetPid = options.ProcessId,
            TargetName = info?.ProcessName,
            TargetStartTime = info?.StartTime,
            TargetOwner = info?.Owner,
            DumpType = options.Type.ToString(),
            Location = AuditRecord.Clean(result.FilePath ?? options.OutputDirectory),
            ErrorDetails = result.Status == DumpStatus.Succeeded ? null : AuditRecord.Clean(result.Message),
            FileSha256 = result.Metadata?.Sha256,
            Encrypted = result.Status == DumpStatus.Succeeded ? result.Metadata?.EncryptionScheme is not null : options.Encrypt || options.Password is not null,
            Justification = options.Admin is { } admin ? AuditRecord.Clean(admin.Reason) : null,
        };

        var problem = await _recorder.TryAppendAsync(record, ct);
        return problem is null
            ? result
            : result with { Warnings = [.. result.Warnings ?? [], problem] };
    }

    private static AuditOutcome OutcomeOf(DumpStatus status) => status switch
    {
        DumpStatus.Succeeded => AuditOutcome.Succeeded,
        DumpStatus.Unauthorized or DumpStatus.AccessDenied => AuditOutcome.Denied,
        DumpStatus.Cancelled => AuditOutcome.Cancelled,
        _ => AuditOutcome.Failed,
    };
}
