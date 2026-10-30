using ProcLens.Core.Models;

namespace ProcLens.Platform.Linux;

public sealed record ProcStat(string Name, char State, int ParentProcessId, long StartTimeTicks, long CpuTicks);

/// <summary>Pure parsers for /proc files, kept separate so they can be unit tested on any OS.</summary>
public static class ProcParser
{
    // /proc/[pid]/stat: "pid (comm) state ppid ... starttime(22) ...". comm may contain spaces and ')'.
    public static ProcStat? ParseStat(string content)
    {
        var open = content.IndexOf('(');
        var close = content.LastIndexOf(')');
        if (open < 0 || close < open) return null;

        var fields = content[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] is field 3 (state): ppid (4) is [1], utime (14) is [11], stime (15) is [12], starttime (22) is [19].
        if (fields.Length < 20
            || !int.TryParse(fields[1], out var ppid)
            || !long.TryParse(fields[11], out var utime)
            || !long.TryParse(fields[12], out var stime)
            || !long.TryParse(fields[19], out var start))
            return null;

        return new ProcStat(content[(open + 1)..close], fields[0][0], ppid, start, utime + stime);
    }

    public static Dictionary<string, string> ParseKeyValues(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) map[line[..idx]] = line[(idx + 1)..].Trim();
        }
        return map;
    }

    /// <summary>Parses "1234 kB" into bytes.</summary>
    public static long? ParseKilobytes(string? value)
    {
        if (value is null) return null;
        var number = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(number, out var kb) ? kb * 1024 : null;
    }

    public static string? ResolveUserName(string passwdContent, string uid)
    {
        foreach (var line in passwdContent.Split('\n'))
        {
            var parts = line.Split(':');
            if (parts.Length > 2 && parts[2] == uid) return parts[0];
        }
        return null;
    }

    public static (ThreadRunState State, string Detail) MapThreadState(char state) => state switch
    {
        'R' => (ThreadRunState.Running, "running (R)"),
        'S' => (ThreadRunState.Sleeping, "interruptible sleep (S)"),
        'D' => (ThreadRunState.Waiting, "uninterruptible disk sleep (D)"),
        'I' => (ThreadRunState.Sleeping, "idle kernel thread (I)"),
        'T' => (ThreadRunState.Stopped, "stopped by signal (T)"),
        't' => (ThreadRunState.Stopped, "stopped by debugger (t)"),
        'Z' => (ThreadRunState.Terminated, "zombie (Z)"),
        'X' => (ThreadRunState.Terminated, "dead (X)"),
        _ => (ThreadRunState.Unknown, $"unrecognised state ({state})"),
    };

    /// <summary>
    /// Derives loaded modules from /proc/[pid]/maps: every file-backed path that has at least one executable
    /// mapping (the main binary and shared libraries). Data files that are merely mmapped are excluded.
    /// Base is the lowest mapped address and Size the span to the highest end, matching an image's footprint.
    /// </summary>
    public static IReadOnlyList<ModuleInfo> ParseMaps(string content)
    {
        var groups = new Dictionary<string, (ulong Start, ulong End, bool Exec)>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n'))
        {
            // address perms offset dev inode [path]
            var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;

            var path = parts[5].Trim();
            if (!path.StartsWith('/')) continue; // anonymous, [heap], [stack], [vdso] ...

            var range = parts[0].Split('-');
            if (range.Length != 2
                || !ulong.TryParse(range[0], System.Globalization.NumberStyles.HexNumber, null, out var start)
                || !ulong.TryParse(range[1], System.Globalization.NumberStyles.HexNumber, null, out var end))
                continue;

            var exec = parts[1].Length > 2 && parts[1][2] == 'x';
            groups[path] = groups.TryGetValue(path, out var g)
                ? (Math.Min(g.Start, start), Math.Max(g.End, end), g.Exec || exec)
                : (start, end, exec);
        }

        return groups
            .Where(kv => kv.Value.Exec)
            .OrderBy(kv => kv.Value.Start)
            .Select(kv => new ModuleInfo(
                Path.GetFileName(kv.Key),
                kv.Key,
                Measurement<ulong>.Of(kv.Value.Start),
                Measurement<long>.Of((long)(kv.Value.End - kv.Value.Start))))
            .ToList();
    }
}
