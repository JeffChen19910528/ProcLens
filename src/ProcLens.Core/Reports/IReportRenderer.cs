namespace ProcLens.Core.Reports;

public interface IReportRenderer
{
    /// <summary>Lower-case format name used on the command line: "json", "html" or "txt".</summary>
    string Format { get; }

    string FileExtension { get; }

    /// <summary>Renders in English.</summary>
    string Render(DiagnosticReport report) => Render(report, EnglishText.Instance);

    /// <summary>Renders with the labels and sentences in the given language. JSON ignores it: the data model is never translated.</summary>
    string Render(DiagnosticReport report, ITextLocalizer text);
}
