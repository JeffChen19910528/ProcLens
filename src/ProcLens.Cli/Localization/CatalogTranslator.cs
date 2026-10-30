using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProcLens.Cli.Hosting;

namespace ProcLens.Cli.Localization;

/// <summary>
/// Translates from a catalog keyed by the English text. The English wording in the code is the key, so a missing
/// entry shows English (never a blank or a key name) and is recorded in <see cref="Misses"/> for tests to catch.
/// </summary>
/// <remarks>
/// Two lookups: <see cref="Format"/> for templates written in the command layer, and <see cref="Localize"/> for finished
/// sentences that arrive from lower layers (error reasons, warnings), which are matched against the catalog's
/// templates with the <c>{n}</c> parts treated as wildcards.
/// </remarks>
public sealed partial class CatalogTranslator : ITranslator
{
    private const int MinimumFixedText = 12;

    private readonly IReadOnlyDictionary<string, string> _catalog;
    private readonly IReadOnlyList<(Regex Pattern, string Translation)> _patterns;
    private readonly ConcurrentDictionary<string, byte> _misses = new();

    public CatalogTranslator(IReadOnlyDictionary<string, string> catalog)
    {
        _catalog = catalog;
        // Only templates with enough fixed wording become patterns: "{0} s" would otherwise match any text ending in " s".
        // (Short templates still work through Format, which looks them up by their exact text.)
        _patterns = catalog
            .Where(e => PlaceholderRegex().IsMatch(e.Key) && PlaceholderRegex().Replace(e.Key, "").Length >= MinimumFixedText)
            .OrderByDescending(e => PlaceholderRegex().Replace(e.Key, "").Length) // the most specific wording wins
            .Select(e => (ToPattern(e.Key), e.Value))
            .ToList();
    }

    /// <summary>English texts that were asked for but are not in the catalog.</summary>
    public IReadOnlyCollection<string> Misses => _misses.Keys.ToList();

    public static CatalogTranslator Load(string language)
    {
        var resource = $"ProcLens.Cli.Localization.Catalogs.{language}.json";
        using var stream = typeof(CatalogTranslator).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"The language catalog '{resource}' is missing from the assembly.");
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                      ?? throw new InvalidOperationException($"The language catalog '{resource}' is empty.");
        return new CatalogTranslator(catalog);
    }

    public string Format(string englishTemplate, object?[] args)
    {
        if (!_catalog.TryGetValue(englishTemplate, out var template))
        {
            // Wording that never needs translating (a lone number, a symbol) is not a miss.
            if (englishTemplate.Any(char.IsLetter)) _misses.TryAdd(englishTemplate, 0);
            template = englishTemplate;
        }
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    public string Localize(string englishMessage)
    {
        if (string.IsNullOrEmpty(englishMessage)) return englishMessage;
        if (Translate(englishMessage) is { } translated) return translated;

        // Text that already contains Chinese was localized by someone else (System.CommandLine's own parse errors), so it is not a gap.
        if (englishMessage.Any(char.IsAsciiLetter) && !englishMessage.Any(c => c >= '⺀')) _misses.TryAdd(englishMessage, 0);
        return englishMessage;
    }

    /// <summary>The translation of a finished sentence, or null when the catalog has nothing for it.</summary>
    private string? Translate(string englishMessage)
    {
        if (_catalog.TryGetValue(englishMessage, out var exact)) return exact;

        foreach (var (pattern, translation) in _patterns)
        {
            var match = pattern.Match(englishMessage);
            if (!match.Success) continue;

            // Captured parts may be sentences themselves ("Access denied: <reason>"), or plain data (a path, a name):
            // translate what the catalog knows and leave the rest alone.
            return PlaceholderRegex().Replace(translation, m =>
            {
                var group = match.Groups["g" + m.Groups[1].Value];
                return group.Success ? Translate(group.Value) ?? group.Value : m.Value;
            });
        }
        return null;
    }

    private static Regex ToPattern(string template)
    {
        var text = new System.Text.StringBuilder("^");
        var last = 0;
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            text.Append(Regex.Escape(template[last..m.Index]));
            var name = "g" + m.Groups[1].Value;
            // A repeated placeholder must be written once as a group and then referenced.
            text.Append(text.ToString().Contains($"(?<{name}>", StringComparison.Ordinal) ? $"\\k<{name}>" : $"(?<{name}>.+?)");
            last = m.Index + m.Length;
        }
        text.Append(Regex.Escape(template[last..])).Append('$');
        return new Regex(text.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();
}
