using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ProcLens.Cli.Commands.Config;
using ProcLens.Cli.Hosting;
using ProcLens.Cli.Localization;

namespace ProcLens.Cli;

public static class CliApplication
{
    /// <summary>Exit code for a failure nobody anticipated (BSD sysexits "software error").</summary>
    public const int UnexpectedErrorExitCode = ExitCodes.Unexpected;

    /// <param name="configure">Lets tests replace services; production passes nothing.</param>
    /// <param name="translatorCreated">Lets tests see which texts a run asked the translator for; production passes nothing.</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct = default, Action<IServiceCollection>? configure = null,
        Action<ITranslator>? translatorCreated = null)
    {
        // The language is chosen before anything is built, because option and command descriptions are translated as the tree is made.
        var choice = LanguageSettings.Resolve(args);
        ITranslator translator = choice.Language == Languages.TraditionalChinese ? CatalogTranslator.Load(choice.Language) : new EnglishTranslator();
        var console = new CliConsole(output, error, translator);
        translatorCreated?.Invoke(translator);

        // System.CommandLine's own help and error text follow the UI culture; restore it so callers are not affected.
        var previousUi = CultureInfo.CurrentUICulture;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = Languages.CultureOf(choice.Language);
        CultureInfo.CurrentCulture = Languages.CultureOf(choice.Language);
        try
        {
            if (choice.Problem is not null) console.ErrorLine(choice.Problem);

            var services = new ServiceCollection().AddProcLens().AddCliCommands();
            services.AddSingleton<ICliConsole>(console);
            services.AddSingleton(new ActiveLanguage(choice));
            configure?.Invoke(services);
            using var provider = services.BuildServiceProvider();

            var root = CliCommands.BuildRoot(provider.GetServices<ICliCommand>(), console);

            // The library's default handler would print a full stack trace to the real stderr. Turn it off so that
            // unexpected failures reach the handler below, and send help and usage errors to the writers we were given.
            var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false, Output = output, Error = error };
            var parsed = root.Parse(args);

            // A usage mistake is reported on stderr only. The library would also print the whole help page to stdout,
            // which would corrupt the output of scripts that read stdout (for example with --json).
            if (parsed.Errors.Count > 0)
            {
                foreach (var problem in parsed.Errors) console.ErrorLine(console.Localize(problem.Message));
                console.ErrorLine(console.T("Run 'proclens --help' (or add --help to a command) for usage."));
                return ExitCodes.NotFound;
            }

            return await parsed.InvokeAsync(configuration, ct);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (Exception e)
        {
            // A clear one-line failure instead of a crash dump with a stack trace. The details are still available
            // to whoever is debugging, but are not printed by default (paths and internals are not for casual output).
            console.ErrorLine(console.T("Unexpected error ({0}): {1}", e.GetType().Name, e.Message));
            console.ErrorLine(Environment.GetEnvironmentVariable("PROCLENS_DEBUG") == "1"
                ? e.ToString()
                : console.T("Set PROCLENS_DEBUG=1 to see the full details, and include them when reporting the problem."));
            return ExitCodes.Unexpected;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previous;
        }
    }
}
