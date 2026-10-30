using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcLens.Core.Reports;

namespace ProcLens.Infrastructure.Reports;

internal static class ReportText
{
    /// <summary>
    /// Reports use LF line endings on every platform, so the same process gives byte-identical structure whether the
    /// report was produced on Windows, Linux or macOS (AppendLine would use the platform's own newline).
    /// </summary>
    public static string Normalise(System.Text.StringBuilder text) => text.ToString().Replace("\r\n", "\n");
}

public static class ReportRenderers
{
    public static readonly IReadOnlyList<IReportRenderer> All = [new JsonReportRenderer(), new HtmlReportRenderer(), new TextReportRenderer()];

    public static IReportRenderer? For(string format) =>
        All.FirstOrDefault(r => string.Equals(r.Format, format, StringComparison.OrdinalIgnoreCase));
}

public sealed class JsonReportRenderer : IReportRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Format => "json";
    public string FileExtension => ".json";

    /// <summary>The full report model. Unavailable values keep their reason; sections keep their status.</summary>
    public string Render(DiagnosticReport report, ITextLocalizer text) => JsonSerializer.Serialize(report, Options);
}

public sealed class TextReportRenderer : IReportRenderer
{
    public string Format => "txt";
    public string FileExtension => ".txt";

    public string Render(DiagnosticReport report, ITextLocalizer words)
    {
        var view = ReportViewBuilder.Build(report, words);
        var text = new StringBuilder();
        text.AppendLine(view.Heading);
        text.AppendLine(view.Subtitle);
        text.AppendLine(new string('=', Math.Max(view.Heading.Length, 60)));

        foreach (var section in view.Sections)
        {
            text.AppendLine();
            text.AppendLine($"{section.Name.ToUpperInvariant()}  [{section.StatusLabel}]");
            text.AppendLine(new string('-', 60));
            if (section.Note is not null) text.AppendLine($"  {section.Note}");

            var width = section.Facts.Count == 0 ? 0 : section.Facts.Max(f => TextWidth.Of(f.Key));
            foreach (var fact in section.Facts)
                text.AppendLine($"  {TextWidth.PadRight(fact.Key, width)} : {fact.Value}");

            foreach (var table in section.Tables.Where(t => t.Rows.Count > 0))
            {
                if (section.Facts.Count > 0) text.AppendLine();
                text.AppendLine($"  {table.Caption}");
                AppendTable(text, table);
            }
        }
        return ReportText.Normalise(text);
    }

    private static void AppendTable(StringBuilder text, ViewTable table)
    {
        // Every column but the last is padded; the last one holds long text (paths, messages) and is left as is.
        // Widths are in terminal columns, so Chinese headings (two columns per character) still line up with their data.
        var widths = table.Columns.Select((c, i) => i == table.Columns.Count - 1 ? 0 : Math.Max(TextWidth.Of(c), table.Rows.Max(r => TextWidth.Of(r[i])))).ToArray();

        string Line(IReadOnlyList<string> cells) => "    " + string.Join("  ", cells.Select((c, i) => widths[i] == 0 ? c : TextWidth.PadRight(c, widths[i]))).TrimEnd();

        text.AppendLine(Line(table.Columns));
        text.AppendLine("    " + string.Join("  ", widths.Select((w, i) => new string('-', w == 0 ? Math.Min(TextWidth.Of(table.Columns[i]), 20) : w))));
        foreach (var row in table.Rows) text.AppendLine(Line(row));
    }
}

public sealed class HtmlReportRenderer : IReportRenderer
{
    public string Format => "html";
    public string FileExtension => ".html";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public string Render(DiagnosticReport report, ITextLocalizer words)
    {
        var view = ReportViewBuilder.Build(report, words);
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine($"<html lang=\"{E(words.LanguageTag)}\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        // The report is self-contained: it needs no scripts, fonts, images or network access, and says so to the browser.
        html.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        html.AppendLine($"<title>{E(view.Title)}</title>");
        html.AppendLine($"<style>{Css}</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"<header><h1>{E(view.Heading)}</h1><p class=\"meta\">{E(view.Subtitle)}</p></header>");

        html.AppendLine($"<nav aria-label=\"{E(words.Text("Sections"))}\"><ul>");
        foreach (var section in view.Sections)
            html.AppendLine($"<li><a href=\"#{Id(section)}\">{E(section.Name)}</a> <span class=\"badge {Kind(section.Kind)}\">{E(section.StatusLabel)}</span></li>");
        html.AppendLine("</ul></nav>");

        html.AppendLine("<main>");
        foreach (var section in view.Sections)
        {
            html.AppendLine($"<section id=\"{Id(section)}\">");
            html.AppendLine($"<h2>{E(section.Name)} <span class=\"badge {Kind(section.Kind)}\">{E(section.StatusLabel)}</span></h2>");
            if (section.Note is not null) html.AppendLine($"<p class=\"note\">{E(section.Note)}</p>");

            if (section.Facts.Count > 0)
            {
                html.AppendLine("<dl>");
                foreach (var fact in section.Facts)
                    html.AppendLine($"<dt>{E(fact.Key)}</dt><dd{(fact.Value.StartsWith("n/a", StringComparison.Ordinal) ? " class=\"na\"" : "")}>{E(fact.Value)}</dd>");
                html.AppendLine("</dl>");
            }

            foreach (var table in section.Tables.Where(t => t.Rows.Count > 0))
            {
                html.AppendLine("<div class=\"scroll\"><table>");
                html.AppendLine($"<caption>{E(table.Caption)}</caption>");
                html.AppendLine("<thead><tr>" + string.Join("", table.Columns.Select(c => $"<th scope=\"col\">{E(c)}</th>")) + "</tr></thead>");
                html.AppendLine("<tbody>");
                foreach (var row in table.Rows)
                    html.AppendLine("<tr>" + string.Join("", row.Select(c => $"<td{(c.StartsWith("n/a", StringComparison.Ordinal) ? " class=\"na\"" : "")}>{E(c)}</td>")) + "</tr>");
                html.AppendLine("</tbody></table></div>");
            }
            html.AppendLine("</section>");
        }
        html.AppendLine("</main>");
        html.AppendLine($"<footer><p class=\"meta\">{E(words.Text("Contains process metadata only. No memory contents are included in this report."))}</p></footer>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return ReportText.Normalise(html);
    }

    private static string Id(ViewSection section) => section.Id.ToLowerInvariant();

    private static string Kind(ViewKind kind) => kind switch
    {
        ViewKind.Ok => "ok",
        ViewKind.Warn => "warn",
        ViewKind.Bad => "bad",
        _ => "muted",
    };

    private const string Css = """
        :root{--bg:#ffffff;--fg:#1f2328;--muted:#59636e;--line:#d1d9e0;--card:#f6f8fa;--ok:#116329;--okbg:#dafbe1;--warn:#7d4e00;--warnbg:#fff8c5;--bad:#a40e26;--badbg:#ffebe9;--mutedbg:#eaeef2}
        @media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--line:#3d444d;--card:#151b23;--ok:#7ee787;--okbg:#12261e;--warn:#e3b341;--warnbg:#272115;--bad:#ff7b72;--badbg:#2d1214;--mutedbg:#212830}}
        *{box-sizing:border-box}
        body{margin:0 auto;max-width:72rem;padding:1.5rem 1rem 3rem;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
        h1{margin:0 0 .25rem;font-size:1.6rem}h2{margin:2rem 0 .5rem;font-size:1.2rem;border-bottom:1px solid var(--line);padding-bottom:.3rem}
        .meta,.note,footer{color:var(--muted)}.note{margin:.25rem 0 .75rem}
        nav ul{display:flex;flex-wrap:wrap;gap:.5rem 1.25rem;list-style:none;padding:0;margin:1rem 0}
        a{color:inherit}
        .badge{display:inline-block;margin-left:.4rem;padding:.05rem .5rem;border-radius:1rem;font-size:.75rem;font-weight:600;vertical-align:middle}
        .badge.ok{color:var(--ok);background:var(--okbg)}.badge.warn{color:var(--warn);background:var(--warnbg)}
        .badge.bad{color:var(--bad);background:var(--badbg)}.badge.muted{color:var(--muted);background:var(--mutedbg)}
        dl{display:grid;grid-template-columns:minmax(10rem,16rem) 1fr;gap:.25rem 1rem;margin:.5rem 0 1rem}
        dt{color:var(--muted)}dd{margin:0;word-break:break-word}.na{color:var(--muted);font-style:italic}
        .scroll{overflow-x:auto;margin:.5rem 0 1rem}
        table{border-collapse:collapse;width:100%;font-size:.9rem}caption{text-align:left;color:var(--muted);padding:.25rem 0}
        th,td{border:1px solid var(--line);padding:.3rem .6rem;text-align:left;vertical-align:top;word-break:break-word}
        thead th{background:var(--card)}
        @media (max-width:40rem){dl{grid-template-columns:1fr}dt{font-weight:600}}
        """;
}
