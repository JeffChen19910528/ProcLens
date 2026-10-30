namespace ProcLens.Core.Security;

/// <summary>
/// Rules for paths the operator asks ProcLens to write to. They do not decide *where* you may write (the operating
/// system does that); they refuse forms that silently write somewhere other than a normal file.
/// </summary>
public static class OutputPathPolicy
{
    private static readonly HashSet<string> DosDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string? Check(string? path) => Check(path, OperatingSystem.IsWindows());

    /// <returns>Why the path is refused, or null if it is acceptable.</returns>
    public static string? Check(string? path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A path is required.";
        if (path.Any(char.IsControl)) return "The path contains control characters.";
        if (!windows) return null;

        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            return "Device paths are not accepted.";

        // A colon is only legitimate as the drive separator ("C:"). Anywhere else it names an NTFS alternate data
        // stream, which would hide the output inside another file.
        var afterDrive = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? path[2..] : path;
        if (afterDrive.Contains(':'))
            return "A colon is only allowed in the drive letter; 'name:stream' would write into an NTFS alternate data stream.";

        foreach (var segment in path.Split('\\', '/'))
        {
            var stem = segment.Contains('.') ? segment[..segment.IndexOf('.')] : segment;
            if (DosDevices.Contains(stem.TrimEnd(' ')))
                return $"'{segment}' is a reserved Windows device name.";
        }
        return null;
    }
}
