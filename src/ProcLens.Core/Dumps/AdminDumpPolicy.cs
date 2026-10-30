using System.Text.Json;

namespace ProcLens.Core.Dumps;

/// <summary>
/// Machine policy for administrative dumps (dumping processes owned by other users). Disabled unless a policy
/// file explicitly enables it. It is an accountability control, not a boundary against administrators: the OS
/// still decides what an elevated token may open.
/// </summary>
public sealed record AdminDumpPolicy(
    bool AdminDumpsEnabled = false,
    IReadOnlyList<string>? AllowedOperators = null,
    bool RequireReason = true,
    int MinReasonLength = 10,
    string? Source = null)
{
    public static AdminDumpPolicy Disabled(string? source = null) => new(Source: source);

    /// <summary>Loads the policy. A missing file means "disabled"; so does an unreadable or invalid one (fail closed).</summary>
    public static AdminDumpPolicy Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Disabled("no policy file");
            if (new FileInfo(path).Length > DumpFiles.MaxMetadataBytes) return Disabled($"{path} (too large to be a policy file)");

            var file = JsonSerializer.Deserialize<PolicyFile>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (file is null) return Disabled($"{path} (empty)");

            return new AdminDumpPolicy(
                file.AdminDumpsEnabled,
                file.AllowedOperators is { Count: > 0 } ops ? ops : null,
                file.RequireReason ?? true,
                Math.Max(1, file.MinReasonLength ?? 10),
                path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Disabled($"{path} (unreadable or invalid: administrative dumps stay disabled)");
        }
    }

    private sealed record PolicyFile(
        bool AdminDumpsEnabled, List<string>? AllowedOperators, bool? RequireReason, int? MinReasonLength);
}

/// <summary>Where ProcLens keeps its policy, audit log and keys. Each can be overridden with an environment variable.</summary>
public static class ProcLensPaths
{
    public const string PolicyVariable = "PROCLENS_POLICY";
    public const string AuditVariable = "PROCLENS_AUDIT_LOG";
    public const string KeyDirectoryVariable = "PROCLENS_KEY_DIR";
    public const string ConfigVariable = "PROCLENS_CONFIG";

    /// <summary>The user's own settings (currently only the display language). Never security-relevant, so a per-user file is fine.</summary>
    public static string ConfigFile => FromEnvironment(ConfigVariable) ??
        Path.Combine(UserDataDirectory, "config.json");

    /// <summary>True when <c>PROCLENS_POLICY</c> names the policy file: an explicit choice by whoever runs ProcLens.</summary>
    public static bool PolicyFileIsOverridden => FromEnvironment(PolicyVariable) is not null;

    public static string PolicyFile => FromEnvironment(PolicyVariable) ??
        (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcLens", "policy.json")
            : "/etc/proclens/policy.json");

    public static string AuditLog => FromEnvironment(AuditVariable) ??
        Path.Combine(UserDataDirectory, "audit", "audit.jsonl");

    public static string KeyDirectory => FromEnvironment(KeyDirectoryVariable) ??
        Path.Combine(UserDataDirectory, "keys");

    private static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcLens");

    private static string? FromEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
