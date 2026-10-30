using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcLens.Core.Dumps;

/// <summary>One allow/deny entry of a Windows ACL, reduced to what the trust decision needs. <c>Rights</c> holds the raw FileSystemRights bits.</summary>
public sealed record AclRule(string Sid, string Name, bool Allow, int Rights, bool InheritOnly);

/// <summary>
/// Decides whether the machine policy file may be believed. The policy can enable administrative dumps and set
/// how long dumps are kept, so it must not be something an ordinary user can plant or edit: on a default Windows
/// install any user may create <c>C:\ProgramData\ProcLens\policy.json</c>. A policy that fails this check is ignored
/// (administrative dumps stay disabled, retention uses its defaults) and the reason is shown by <c>proclens policy show</c>.
/// </summary>
public static class PolicyFileTrust
{
    private const string System = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string CreatorOwner = "S-1-3-0";

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase) { System, Administrators, TrustedInstaller };

    // FileSystemRights bits, spelled out so this decision is plain arithmetic that can be tested on any platform.
    public const int WriteData = 0x2, AppendData = 0x4, DeleteSubdirectoriesAndFiles = 0x40,
                     Delete = 0x10000, ChangePermissions = 0x40000, TakeOwnership = 0x80000;

    // Rights that let a principal change the file's content, replace it, or take it over.
    private const int FileMask = WriteData | AppendData | Delete | ChangePermissions | TakeOwnership;

    // For the containing folder: creating files (WriteData) or deleting/replacing entries matters; creating
    // sub-folders (AppendData) does not.
    private const int DirectoryMask = WriteData | Delete | DeleteSubdirectoriesAndFiles | ChangePermissions | TakeOwnership;

    /// <returns>Null if the file is trustworthy or absent; otherwise why it must be ignored.</returns>
    public static string? WhyUntrusted(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "it is a symbolic link";
            if (OperatingSystem.IsWindows()) return CheckWindows(path);
            return CheckUnix(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"its permissions could not be checked ({e.Message})";
        }
    }

    /// <summary>The pure decision, separated so every case can be tested without a real ACL.</summary>
    public static string? EvaluateWindows(
        (string Sid, string Name) fileOwner, IReadOnlyList<AclRule> fileRules,
        (string Sid, string Name) directoryOwner, IReadOnlyList<AclRule> directoryRules)
    {
        if (!Trusted.Contains(fileOwner.Sid))
            return $"it is owned by {fileOwner.Name}, not by Administrators or SYSTEM";
        if (!Trusted.Contains(directoryOwner.Sid))
            return $"its folder is owned by {directoryOwner.Name}, not by Administrators or SYSTEM";

        foreach (var rule in fileRules)
            if (Grants(rule, FileMask)) return $"{rule.Name} can modify it";
        foreach (var rule in directoryRules)
            if (Grants(rule, DirectoryMask)) return $"{rule.Name} can add, replace or delete files in its folder";
        return null;
    }

    private static bool Grants(AclRule rule, int mask) =>
        rule.Allow && !rule.InheritOnly && (rule.Rights & mask) != 0 &&
        !Trusted.Contains(rule.Sid) && !string.Equals(rule.Sid, CreatorOwner, StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? CheckWindows(string path)
    {
        var file = new FileInfo(path);
        var directory = file.Directory ?? throw new InvalidOperationException("The policy file has no folder.");
        return EvaluateWindows(Owner(file.GetAccessControl()), Rules(file.GetAccessControl()),
                               Owner(directory.GetAccessControl()), Rules(directory.GetAccessControl()));
    }

    [SupportedOSPlatform("windows")]
    private static (string, string) Owner(FileSystemSecurity security)
    {
        var sid = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new InvalidOperationException("No owner.");
        return (sid.Value, Name(sid));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AclRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => new AclRule(
                ((SecurityIdentifier)r.IdentityReference).Value, Name((SecurityIdentifier)r.IdentityReference),
                r.AccessControlType == AccessControlType.Allow, (int)r.FileSystemRights,
                (r.PropagationFlags & PropagationFlags.InheritOnly) != 0))
            .ToList();

    [SupportedOSPlatform("windows")]
    private static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
    }

    /// <summary>
    /// On Unix the default location is under /etc, which only root can change, so the check is the mode: nobody
    /// but the owner may write the file or its folder. (The owner is not checked: .NET exposes no portable owner.)
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static string? CheckUnix(string path)
    {
        const UnixFileMode others = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(path) & others) != 0) return "it is writable by group or others";

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null && (File.GetUnixFileMode(directory) & others) != 0)
            return "its folder is writable by group or others";
        return null;
    }
}
