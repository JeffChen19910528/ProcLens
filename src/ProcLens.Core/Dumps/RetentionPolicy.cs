using System.Text.Json;

namespace ProcLens.Core.Dumps;

/// <summary>
/// How long ProcLens-created dumps may be kept. Rules combine: a dump is eligible for removal if <b>any</b> of them
/// says so. Nothing is ever removed automatically; the policy is only applied by an explicit <c>dump prune</c>.
/// </summary>
/// <param name="MaxAgeDays">Dumps older than this are eligible. 0 makes every dump eligible.</param>
/// <param name="MaxCount">Keep at most this many dumps (the newest).</param>
/// <param name="MaxTotalBytes">Keep the newest dumps whose combined size fits within this limit.</param>
public sealed record RetentionPolicy(int MaxAgeDays = 30, int? MaxCount = null, long? MaxTotalBytes = null, string? Source = null)
{
    public const int DefaultMaxAgeDays = 30;

    /// <summary>
    /// Reads the "Retention" section of the machine policy file. A missing file or section gives the defaults; an
    /// invalid one also gives the defaults (never an empty policy that would keep or delete everything by accident).
    /// </summary>
    public static RetentionPolicy Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new RetentionPolicy(Source: "defaults (no policy file)");
            if (new FileInfo(path).Length > DumpFiles.MaxMetadataBytes) return new RetentionPolicy(Source: $"defaults ({path} is too large to be a policy file)");

            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var section = FindObject(document.RootElement, "Retention");
            if (section is null)
                return new RetentionPolicy(Source: $"defaults ({path} has no Retention section)");

            var days = Read(section.Value, "MaxAgeDays");
            var count = Read(section.Value, "MaxCount");
            var megabytes = Read(section.Value, "MaxTotalMegabytes");
            if (days is < 0 || count is < 0 || megabytes is < 0)
                return new RetentionPolicy(Source: $"defaults ({path} has a negative Retention value)");

            return new RetentionPolicy(
                days is { } d ? (int)Math.Min(d, 36500) : DefaultMaxAgeDays,
                count is { } c ? (int)Math.Min(c, int.MaxValue) : null,
                megabytes is { } m ? m * 1024 * 1024 : null,
                path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RetentionPolicy(Source: $"defaults ({path} is unreadable or invalid)");
        }
    }

    private static JsonElement? FindObject(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                return property.Value;
        return null;
    }

    private static long? Read(JsonElement section, string name)
    {
        foreach (var property in section.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
                return value;
        return null;
    }
}

/// <summary>A dump (and its metadata file) that ProcLens created and can therefore manage.</summary>
public sealed record ManagedDump(string DumpPath, string SidecarPath, DumpMetadata Metadata, long Bytes);

public sealed record RetentionCandidate(ManagedDump Dump, string Reason);

/// <summary>Decides which managed dumps fall outside a policy. Pure, so every rule is unit tested without a file system.</summary>
public static class RetentionPlanner
{
    /// <returns>The dumps to remove, oldest first, each with the first rule that selected it.</returns>
    public static IReadOnlyList<RetentionCandidate> Plan(IReadOnlyCollection<ManagedDump> dumps, RetentionPolicy policy, DateTimeOffset now)
    {
        var newestFirst = dumps.OrderByDescending(d => d.Metadata.CreationTime).ToList();
        var reasons = new Dictionary<ManagedDump, string>();

        foreach (var dump in newestFirst)
        {
            var age = now - dump.Metadata.CreationTime;
            if (age > TimeSpan.FromDays(policy.MaxAgeDays))
                reasons[dump] = $"older than {policy.MaxAgeDays} day(s) (created {dump.Metadata.CreationTime:yyyy-MM-dd})";
        }

        if (policy.MaxCount is { } maxCount)
        {
            foreach (var dump in newestFirst.Skip(maxCount))
                reasons.TryAdd(dump, $"beyond the newest {maxCount} dump(s)");
        }

        if (policy.MaxTotalBytes is { } limit)
        {
            long total = 0;
            var exceeded = false;
            foreach (var dump in newestFirst.Where(d => !reasons.ContainsKey(d)))
            {
                total += dump.Bytes;
                if (total > limit) exceeded = true;
                if (exceeded) reasons.TryAdd(dump, $"total size would exceed {limit / (1024 * 1024)} MB");
            }
        }

        return reasons
            .OrderBy(kv => kv.Key.Metadata.CreationTime)
            .Select(kv => new RetentionCandidate(kv.Key, kv.Value))
            .ToList();
    }
}
