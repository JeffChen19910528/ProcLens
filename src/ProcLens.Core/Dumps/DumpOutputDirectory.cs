using ProcLens.Core.Security;

namespace ProcLens.Core.Dumps;

/// <summary>The output directory of a dump, ready to write into, or the reason it cannot be used.</summary>
public sealed record PreparedDirectory(string? Path, IReadOnlyList<string> Warnings, string? Problem)
{
    public bool IsUsable => Path is not null;
}

/// <summary>
/// Validates and prepares the directory a dump goes into. It is platform neutral: the only operating-system
/// specifics (private permissions, ACL inspection) live behind <see cref="RestrictedFile"/>.
/// </summary>
public static class DumpOutputDirectory
{
    public static PreparedDirectory Prepare(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return Refused("An output directory is required.");
        if (OutputPathPolicy.Check(requested) is { } badPath) return Refused(badPath);

        try
        {
            var full = Path.GetFullPath(requested);
            if (File.Exists(full)) return Refused($"'{full}' is a file, not a directory.");

            IReadOnlyList<string> warnings = [];
            if (Directory.Exists(full))
            {
                if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
                    return Refused($"'{full}' is a symbolic link or junction; choose a real directory so the dump cannot be redirected.");

                // The dump files themselves are always restricted; this only flags that the folder is broadly visible.
                var readers = RestrictedFile.BroadReaders(full);
                if (readers.Count > 0)
                    warnings = [$"The output directory can be listed by {string.Join(", ", readers)}. The dump files are restricted to your account, " +
                               "but their names (process name and id) are visible. Consider a private directory."];
            }
            else
            {
                // A directory that ProcLens creates is private to the current user from the start.
                RestrictedFile.CreateDirectory(full);
            }

            return new PreparedDirectory(full, warnings, null);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or IOException or UnauthorizedAccessException)
        {
            return Refused($"The output directory is not usable: {e.Message}");
        }
    }

    private static PreparedDirectory Refused(string problem) => new(null, [], problem);
}

/// <summary>Names of the files a dump produces. Generated here only, never taken from the caller, so a name cannot escape the directory.</summary>
public static class DumpFileNames
{
    /// <summary><c>&lt;name&gt;_&lt;pid&gt;_&lt;type&gt;_&lt;UTC time&gt;_&lt;random&gt;</c> without an extension.</summary>
    public static string Stem(string processName, int processId, Abstractions.DumpType type, DateTimeOffset now)
    {
        var safe = new string(processName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(40).ToArray());
        if (safe.Length == 0) safe = "process";
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var unique = Guid.NewGuid().ToString("N")[..8];
        return $"{safe}_{processId}_{type.ToString().ToLowerInvariant()}_{stamp}_{unique}";
    }

    /// <summary>Where the raw dump is written. When it will be encrypted it is a temporary name that is wiped afterwards.</summary>
    public static string RawPath(string directory, string stem, bool willBeEncrypted) =>
        Path.Combine(directory, stem + (willBeEncrypted ? ".dmp.plain.tmp" : ".dmp"));

    public static string FinalPath(string directory, string stem, bool encrypted) =>
        Path.Combine(directory, stem + (encrypted ? ".dmp.enc" : ".dmp"));
}
