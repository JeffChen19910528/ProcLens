using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Platform;

namespace ProcLens.Core.Abstractions;

public enum DumpType
{
    Mini,
    Heap,
    Full,
}

public enum DumpStatus
{
    Succeeded,
    PlatformNotSupported,
    InvalidRequest,
    ProcessNotFound,
    Unauthorized,
    AccessDenied,
    ArchitectureMismatch,
    Cancelled,
    Failed,
}

/// <summary>Bytes written so far. The final size is unknown in advance, so no percentage is offered.</summary>
public readonly record struct DumpProgress(long BytesWritten);

/// <param name="ExpectedStartTime">
/// Start time the caller observed for <paramref name="ProcessId"/>. Required: it is how a reused PID is detected.
/// </param>
/// <param name="Encrypt">Protect the dump with this machine/user's key (authenticated encryption at rest).</param>
/// <param name="Password">Additionally allow the dump to be opened with this password (portable across machines).</param>
/// <param name="Admin">Explicit administrative authorization, needed only for processes owned by someone else.</param>
public sealed record DumpRequest(
    int ProcessId,
    DumpType Type,
    string OutputDirectory,
    DateTimeOffset? ExpectedStartTime = null,
    string? ProcessName = null,
    IProgress<DumpProgress>? Progress = null,
    bool Encrypt = false,
    SecretText? Password = null,
    AdminAuthorization? Admin = null);

public sealed record DumpResult(
    DumpStatus Status,
    string? FilePath = null,
    string? Message = null,
    DumpMetadata? Metadata = null,
    AuthorizationDecision? Authorization = null,
    IReadOnlyList<string>? Warnings = null);

public interface IProcessDumpProvider
{
    PlatformKind Platform { get; }
    bool IsSupported { get; }
    Task<DumpResult> CreateDumpAsync(DumpRequest request, CancellationToken ct = default);
}
