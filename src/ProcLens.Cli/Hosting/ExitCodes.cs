using ProcLens.Application.Dumps;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Models;

namespace ProcLens.Cli.Hosting;

/// <summary>
/// The process exit codes, defined once. They are part of ProcLens's contract (documented in CLI_USAGE.md and covered
/// by tests), so every mapping from an outcome to a code lives here and nowhere else.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int NotFound = 1;           // also: a command-line syntax error
    public const int NotSupported = 2;       // or an invalid request
    public const int Denied = 3;             // access denied, not authorized, cannot be unlocked
    public const int ArchitectureMismatch = 4;
    public const int Failed = 5;             // failed, modified, corrupt, tampered
    public const int Unexpected = 70;        // a bug
    public const int Cancelled = 130;

    public static int For(InspectionStatus status) => status switch
    {
        InspectionStatus.Available => Success,
        InspectionStatus.ProcessNotFound => NotFound,
        InspectionStatus.NotSupported => NotSupported,
        InspectionStatus.ArchitectureMismatch => ArchitectureMismatch,
        _ => Denied,
    };

    public static int For(DumpStatus status) => status switch
    {
        DumpStatus.Succeeded => Success,
        DumpStatus.ProcessNotFound => NotFound,
        DumpStatus.PlatformNotSupported or DumpStatus.InvalidRequest => NotSupported,
        DumpStatus.Unauthorized or DumpStatus.AccessDenied => Denied,
        DumpStatus.ArchitectureMismatch => ArchitectureMismatch,
        DumpStatus.Cancelled => Cancelled,
        _ => Failed,
    };

    public static int For(VaultStatus status) => status switch
    {
        VaultStatus.Succeeded => Success,
        VaultStatus.NotFound => NotFound,
        VaultStatus.NotEncrypted or VaultStatus.InvalidRequest => NotSupported,
        VaultStatus.CannotUnlock => Denied,
        _ => Failed,
    };
}
