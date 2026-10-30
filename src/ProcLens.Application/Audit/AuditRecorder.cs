using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;

namespace ProcLens.Application.Audit;

/// <summary>Builds audit records for the current operator and writes them, turning I/O failures into messages.</summary>
internal sealed class AuditRecorder(IAuditLog audit, IOperatorContext operatorContext, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public AuditRecord NewRecord(string operation, AuditAuthorization authorization, AuditOutcome outcome) => new(
        Id: Guid.NewGuid(),
        Timestamp: _time.GetUtcNow(),
        Operator: operatorContext.Identity,
        OperatorElevated: operatorContext.IsElevated,
        Machine: operatorContext.Machine,
        Operation: operation,
        TargetPid: null,
        TargetName: null,
        TargetStartTime: null,
        TargetOwner: null,
        DumpType: null,
        Location: null,
        Authorization: authorization,
        Outcome: outcome,
        ErrorDetails: null,
        FileSha256: null,
        Encrypted: null,
        Justification: null);

    /// <returns>A message if the audit trail cannot be written (the caller must then refuse to proceed), otherwise null.</returns>
    public async Task<string?> CheckAvailableAsync(CancellationToken ct)
    {
        try
        {
            await audit.EnsureAvailableAsync(ct);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The audit log is not writable, so the operation was not performed: {e.Message}";
        }
    }

    /// <returns>A warning if the record could not be written, otherwise null.</returns>
    public async Task<string?> TryAppendAsync(AuditRecord record, CancellationToken ct)
    {
        try
        {
            await audit.AppendAsync(record, ct);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"WARNING: the audit record for this operation could not be written: {e.Message}";
        }
    }
}
