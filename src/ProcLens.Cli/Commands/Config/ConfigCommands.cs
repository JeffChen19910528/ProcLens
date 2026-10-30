using System.CommandLine;
using System.Text.Json;
using ProcLens.Cli.Hosting;
using ProcLens.Cli.Localization;

namespace ProcLens.Cli.Commands.Config;

/// <summary>The language in effect for this run, decided in <see cref="CliApplication"/> before any command is built.</summary>
public sealed record ActiveLanguage(LanguageChoice Choice);

internal sealed class ConfigShowCommand(ActiveLanguage active, ICliConsole console) : ICliCommand
{
    public string Group => "config";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var command = new Command("show", console.T("Show the display language and where it comes from.")) { json };
        command.SetAction(parse =>
        {
            var choice = active.Choice;
            if (parse.GetValue(json))
            {
                // Machine-readable output stays English whatever the display language is.
                console.Line(JsonSerializer.Serialize(new { choice.Language, choice.Source, Available = Languages.All }, Display.Json));
                return ExitCodes.Success;
            }

            console.Line(Display.PadRight(console.T("Language:"), 12) + choice.Language);
            console.Line(Display.PadRight(console.T("Source:"), 12) + console.T(choice.Source));
            console.Line(Display.PadRight(console.T("Available:"), 12) + string.Join(", ", Languages.All));
            return ExitCodes.Success;
        });
        return command;
    }
}

internal sealed class ConfigSetLanguageCommand(ICliConsole console) : ICliCommand
{
    public string Group => "config";

    public Command Build()
    {
        var language = new Argument<string>("language") { Description = console.T("Language to save: en, zh-TW or auto.") };
        var command = new Command("set-language", console.T("Save the display language (en, zh-TW, or auto to clear the saved setting).")) { language };
        command.SetAction(parse =>
        {
            var given = parse.GetValue(language)!;
            var auto = string.Equals(given, "auto", StringComparison.OrdinalIgnoreCase);
            var normalized = auto ? null : Languages.Normalize(given);
            if (!auto && normalized is null)
            {
                console.ErrorLine(console.T("Unknown language '{0}'. Available: {1}.", given, string.Join(", ", Languages.All)));
                return ExitCodes.NotSupported;
            }

            try
            {
                LanguageSettings.Save(normalized);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                console.ErrorLine(console.T("Could not save the setting: {0}", e.Message));
                return ExitCodes.Failed;
            }

            // Confirm in the language that was just chosen, so the person sees it took effect.
            if (normalized is null) console.Line(console.T("Saved display language cleared; English is used unless PROCLENS_LANG or --lang says otherwise."));
            else console.Line(CatalogFor(normalized).Format("Display language saved: {0}.", [normalized]));
            return ExitCodes.Success;
        });
        return command;
    }

    private static ITranslator CatalogFor(string language) =>
        language == Languages.TraditionalChinese ? CatalogTranslator.Load(language) : new EnglishTranslator();
}
