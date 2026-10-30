using System.ComponentModel;

namespace ProcLens.Core.Models;

/// <summary>
/// Reads a single property of another process. The operating system may refuse (access denied) or the process may
/// have gone away between listing and reading; both are normal and mean "this value is unavailable", not "fail".
/// </summary>
public static class Probe
{
    public static T? TryGet<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception e) when (IsExpected(e)) { return null; }
    }

    public static T? TryGetRef<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception e) when (IsExpected(e)) { return null; }
    }

    private static bool IsExpected(Exception e) => e is Win32Exception or InvalidOperationException or NotSupportedException;
}
