namespace ProcLens.Cli.Hosting;

/// <summary>
/// The single door through which the command layer talks to the user. Commands never touch <see cref="Console"/>:
/// everything goes through here, so output can be captured in tests, kept apart (results on stdout, problems on
/// stderr), and translated at one place.
/// </summary>
public interface ICliConsole
{
    /// <summary>Results go here. With --json this carries nothing but JSON.</summary>
    TextWriter Out { get; }

    /// <summary>Problems, warnings, progress and usage mistakes go here.</summary>
    TextWriter Error { get; }

    /// <summary>
    /// Translates a message written in English with <c>{0}</c> placeholders and fills in the arguments. In English this
    /// is just <see cref="string.Format(string, object[])"/>.
    /// </summary>
    string T(string english, params object?[] args);

    /// <summary>Translates a ready-made English sentence that came from a lower layer (an error reason, a warning).</summary>
    string Localize(string message);

    void Line(string text = "");
    void ErrorLine(string text);
}

/// <summary>Translates user-facing text. The default leaves English alone.</summary>
public interface ITranslator
{
    /// <summary>Looks up an English template (with <c>{0}</c> placeholders) and formats it.</summary>
    string Format(string englishTemplate, object?[] args);

    /// <summary>Translates a finished English sentence; unknown sentences come back unchanged.</summary>
    string Localize(string englishMessage);
}

public sealed class EnglishTranslator : ITranslator
{
    public string Format(string englishTemplate, object?[] args) =>
        args.Length == 0 ? englishTemplate : string.Format(System.Globalization.CultureInfo.CurrentCulture, englishTemplate, args);

    public string Localize(string englishMessage) => englishMessage;
}

public sealed class CliConsole(TextWriter output, TextWriter error, ITranslator? translator = null) : ICliConsole
{
    private readonly ITranslator _translator = translator ?? new EnglishTranslator();

    public TextWriter Out => output;
    public TextWriter Error => error;

    public string T(string english, params object?[] args) => _translator.Format(english, args);
    public string Localize(string message) => _translator.Localize(message);

    public void Line(string text = "") => output.WriteLine(text);
    public void ErrorLine(string text) => error.WriteLine(text);
}
