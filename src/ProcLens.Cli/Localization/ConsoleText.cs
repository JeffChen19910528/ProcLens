using ProcLens.Cli.Hosting;
using ProcLens.Core.Reports;

namespace ProcLens.Cli.Localization;

/// <summary>Lets the report renderers (which know nothing about the CLI) speak through the console's translator.</summary>
internal sealed class ConsoleText(ICliConsole console, string languageTag) : ITextLocalizer
{
    public string LanguageTag => languageTag;

    public string Text(string english, params object?[] args) => console.T(english, args);

    public string Localize(string english) => console.Localize(english);
}
