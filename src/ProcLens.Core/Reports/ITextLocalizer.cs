namespace ProcLens.Core.Reports;

/// <summary>
/// Supplies the words a report is written in. The report model and JSON are never translated; only the labels and
/// sentences that the text and HTML views add around the data pass through here. English is the identity.
/// </summary>
public interface ITextLocalizer
{
    /// <summary>BCP 47 tag of the language, for example <c>en</c>; used for the <c>lang</c> attribute of HTML reports.</summary>
    string LanguageTag { get; }

    /// <summary>Translates an English template (with <c>{0}</c> placeholders) and fills in the arguments.</summary>
    string Text(string english, params object?[] args);

    /// <summary>Translates a finished English sentence that came from a lower layer; unknown text is returned unchanged.</summary>
    string Localize(string english);
}

public sealed class EnglishText : ITextLocalizer
{
    public static readonly EnglishText Instance = new();

    public string LanguageTag => "en";

    public string Text(string english, params object?[] args) =>
        args.Length == 0 ? english : string.Format(System.Globalization.CultureInfo.CurrentCulture, english, args);

    public string Localize(string english) => english;
}
