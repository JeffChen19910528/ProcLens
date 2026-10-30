using System.Globalization;
using System.Text.Json;
using ProcLens.Core.Dumps;

namespace ProcLens.Cli.Localization;

/// <summary>The display languages ProcLens ships. English is the default and the language of all machine-readable output.</summary>
public static class Languages
{
    public const string English = "en";
    public const string TraditionalChinese = "zh-TW";

    public static readonly IReadOnlyList<string> All = [English, TraditionalChinese];

    /// <summary>Maps what a person might type (<c>zh</c>, <c>zh_tw</c>, <c>EN</c>) to a shipped language, or null if there is none.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().Replace('_', '-').ToLowerInvariant();
        return v switch
        {
            "en" or "en-us" or "en-gb" => English,
            "zh-tw" or "zh-hant" or "zh-hant-tw" or "zh" => TraditionalChinese,
            _ => null,
        };
    }

    public static CultureInfo CultureOf(string language) =>
        CultureInfo.GetCultureInfo(language == TraditionalChinese ? "zh-TW" : "en-US");
}

public sealed record LanguageChoice(string Language, string Source, string? Problem = null);

/// <summary>
/// Decides the display language. Precedence, highest first: <c>--lang</c>, the <c>PROCLENS_LANG</c> environment
/// variable, the saved setting (<c>config set-language</c>), then English. The operating system's language is
/// deliberately not consulted, so the same command prints the same thing on every machine.
/// </summary>
public static class LanguageSettings
{
    public const string EnvironmentVariable = "PROCLENS_LANG";

    public static LanguageChoice Resolve(string[] args)
    {
        if (FromArguments(args) is { } given)
            return Languages.Normalize(given) is { } l
                ? new LanguageChoice(l, "--lang")
                : new LanguageChoice(Languages.English, "default", $"Unknown language '{given}'. Available: {string.Join(", ", Languages.All)}.");

        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env)
            return Languages.Normalize(env) is { } l
                ? new LanguageChoice(l, EnvironmentVariable)
                : new LanguageChoice(Languages.English, "default", $"Unknown language '{env}' in {EnvironmentVariable}. Available: {string.Join(", ", Languages.All)}.");

        return Languages.Normalize(ReadSaved()) is { } saved
            ? new LanguageChoice(saved, "config")
            : new LanguageChoice(Languages.English, "default");
    }

    /// <summary>Finds <c>--lang value</c> or <c>--lang=value</c> without a full parse (the command tree must be built in the right language first).</summary>
    internal static string? FromArguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--lang") return i + 1 < args.Length ? args[i + 1] : null;
            if (args[i].StartsWith("--lang=", StringComparison.Ordinal)) return args[i]["--lang=".Length..];
        }
        return null;
    }

    public static string? ReadSaved()
    {
        try
        {
            var path = ProcLensPaths.ConfigFile;
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // a damaged setting must never stop the tool: fall back to English
        }
    }

    /// <summary>Saves the language, or removes the setting when <paramref name="language"/> is null.</summary>
    public static void Save(string? language)
    {
        var path = ProcLensPaths.ConfigFile;
        if (language is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { language }));
        File.Move(temp, path, overwrite: true);
    }
}
