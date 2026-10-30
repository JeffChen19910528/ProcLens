using System.CommandLine;
using System.Text;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Reports;
using ProcLens.Cli.Commands.Config;
using ProcLens.Cli.Hosting;
using ProcLens.Cli.Localization;
using ProcLens.Core.Reports;
using ProcLens.Core.Security;
using ProcLens.Infrastructure.Reports;

namespace ProcLens.Cli.Commands.Reports;

internal sealed class ReportGenerateCommand(IReportService reports, ActiveLanguage active, ICliConsole console) : ICliCommand
{
    public string Group => "report";

    public Command Build()
    {
        var pid = CommonOptions.Pid(console);
        var format = new Option<string>("--format") { Description = console.T("json, html or txt."), DefaultValueFactory = _ => "txt" };
        format.AcceptOnlyFromAmong("json", "html", "txt");
        var target = new Option<string?>("--output") { Description = console.T("Write the report to this new file instead of standard output (never overwrites).") };
        var samples = new Option<int>("--samples") { Description = console.T("CPU/memory samples to take (at least 2 for a CPU rate)."), DefaultValueFactory = _ => 3 };
        samples.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > 120) r.AddError(console.T("--samples must be between 1 and 120."));
        });
        var interval = new Option<double>("--interval") { Description = console.T("Seconds between samples (0.1 or more)."), DefaultValueFactory = _ => 1.0 };
        interval.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<double>() < 0.1) r.AddError(console.T("--interval must be at least 0.1 seconds."));
        });
        var dumpsDir = new Option<string?>("--dumps-dir") { Description = console.T("Also list dumps of this process recorded in this directory (Windows only).") };
        var noThreads = new Option<bool>("--no-threads") { Description = console.T("Leave out the thread listing.") };
        var noModules = new Option<bool>("--no-modules") { Description = console.T("Leave out the module listing.") };

        var command = new Command("generate", console.T("Generate a diagnostic report for one process."))
        {
            pid, format, target, samples, interval, dumpsDir, noThreads, noModules,
        };
        command.SetAction(async (parse, ct) =>
        {
            var renderer = ReportRenderers.For(parse.GetValue(format)!)!;
            var path = parse.GetValue(target);
            if (path is not null && OutputPathPolicy.Check(path) is { } badPath)
            {
                console.ErrorLine(console.Localize(badPath));
                return ExitCodes.NotSupported;
            }
            if (path is not null && File.Exists(Path.GetFullPath(path)))
            {
                console.ErrorLine(console.T("'{0}' already exists; existing files are never overwritten.", Path.GetFullPath(path)));
                return ExitCodes.NotSupported;
            }

            var report = await reports.CollectAsync(new ReportOptions(
                parse.GetValue(pid), parse.GetValue(samples), TimeSpan.FromSeconds(parse.GetValue(interval)),
                parse.GetValue(dumpsDir), !parse.GetValue(noThreads), !parse.GetValue(noModules)), ct);

            // A report about nothing is not useful: say so instead of writing an empty document.
            if (report.Process.Status == SectionStatus.ProcessNotFound)
            {
                console.ErrorLine(console.Localize(report.Process.Reason ?? ""));
                return ExitCodes.NotFound;
            }

            var text = renderer.Render(report, new ConsoleText(console, active.Choice.Language));
            if (path is null)
            {
                console.Out.Write(text);
                return ExitCodes.Success;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (Path.GetDirectoryName(full) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
                await using var file = RestrictedFile.CreateNew(full);
                await file.WriteAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), ct);
                console.ErrorLine(console.T("Report written to {0} ({1} warning(s), {2} collection error(s)).", full, report.Warnings.Count, report.Errors.Count));
                return ExitCodes.Success;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                console.ErrorLine(console.T("Could not write the report: {0}", e.Message));
                return ExitCodes.Failed;
            }
        });
        return command;
    }
}
