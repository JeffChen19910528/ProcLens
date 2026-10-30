namespace ProcLens.Core.Models;

public enum InspectionStatus
{
    Available,
    NotSupported,
    AccessDenied,
    ProcessNotFound,

    /// <summary>The inspector and the target differ in bitness in a way the OS does not allow (32-bit inspector, 64-bit target).</summary>
    ArchitectureMismatch,
}

/// <summary>
/// Outcome of a per-process inspection. "Could not read" is an explicit status, never an empty list.
/// </summary>
public sealed record InspectionResult<T>(InspectionStatus Status, IReadOnlyList<T> Items, string? Reason = null)
{
    public static InspectionResult<T> Ok(IReadOnlyList<T> items) => new(InspectionStatus.Available, items);
    public static InspectionResult<T> NotSupported(string reason) => new(InspectionStatus.NotSupported, [], reason);
    public static InspectionResult<T> Denied(string reason) => new(InspectionStatus.AccessDenied, [], reason);
    public static InspectionResult<T> ArchitectureMismatch(string reason) => new(InspectionStatus.ArchitectureMismatch, [], reason);
    public static InspectionResult<T> NotFound() => new(InspectionStatus.ProcessNotFound, [], "Process not found or already exited.");
}
