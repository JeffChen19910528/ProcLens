using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcLens.Core.Security;

/// <summary>
/// Creates files and directories that only the current user can access: a single ACL entry with inheritance
/// removed on Windows, mode 0600/0700 elsewhere. Existing files are never overwritten by <see cref="CreateNew"/>.
/// </summary>
public static class RestrictedFile
{
    /// <summary>Creates a new file, failing with <see cref="IOException"/> if it already exists. Not shared while open.</summary>
    public static FileStream CreateNew(string path) => Open(path, FileMode.CreateNew, FileShare.None);

    /// <summary>Opens the file for reading and writing, creating it with restricted access if it does not exist.</summary>
    public static FileStream OpenOrCreate(string path, FileShare share) => Open(path, FileMode.OpenOrCreate, share);

    private static FileStream Open(string path, FileMode mode, FileShare share)
    {
        if (OperatingSystem.IsWindows()) return OpenWindows(path, mode, share);

        return new FileStream(path, new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = share,
            BufferSize = 1 << 16,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    /// <summary>
    /// Best-effort removal of sensitive data: overwrites the file with zeros, flushes, then deletes it. This reduces
    /// exposure but cannot guarantee erasure on SSDs, journaling or copy-on-write file systems, or snapshotted volumes.
    /// </summary>
    public static void SecureDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            // Overwriting opens the path for writing and would follow a link to whatever it points at. A link is
            // only ever removed, never written through.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                File.Delete(path);
                return;
            }

            // Shared with other readers on purpose: if a scanner or backup tool holds the file open, exclusive access
            // would make the overwrite fail silently and leave the content intact.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var zeros = new byte[1 << 16];
                for (long remaining = stream.Length; remaining > 0; remaining -= zeros.Length)
                    stream.Write(zeros, 0, (int)Math.Min(zeros.Length, remaining));
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // fall through to deletion; nothing better can be done
        }

        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Creates the directory (and any missing parents); the leaf is restricted to the current user.</summary>
    public static void CreateDirectory(string path)
    {
        if (Directory.Exists(path)) return;

        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

        if (OperatingSystem.IsWindows()) CreateDirectoryWindows(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Lists well-known broad groups (Everyone, Users, Authenticated Users) that can list or read the directory.
    /// Always empty on non-Windows platforms.
    /// </summary>
    public static IReadOnlyList<string> BroadReaders(string directory)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(directory)) return [];
        try { return BroadReadersWindows(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenWindows(string path, FileMode mode, FileShare share)
    {
        const FileSystemRights rights =
            FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.ReadAttributes | FileSystemRights.WriteAttributes |
            FileSystemRights.ReadExtendedAttributes | FileSystemRights.WriteExtendedAttributes;

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));

        return FileSystemAclExtensions.Create(new FileInfo(path), mode, rights, share, 1 << 16, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateDirectoryWindows(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.CreateDirectory(security, path);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> BroadReadersWindows(string directory)
    {
        var broad = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
        };
        const FileSystemRights reading = FileSystemRights.ListDirectory | FileSystemRights.ReadData | FileSystemRights.Read;

        return new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & reading) != 0)
            .Where(r => broad.Contains((SecurityIdentifier)r.IdentityReference))
            .Select(r => r.IdentityReference.Translate(typeof(NTAccount)).Value)
            .Distinct()
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user's SID could not be determined.");
}
