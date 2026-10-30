using System.CommandLine;
using System.Text.Json;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Dumps;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Platform;
using ProcLens.Core.Dumps;

namespace ProcLens.Cli.Commands.Dumps;

internal sealed class DumpPruneCommand(IDumpRetentionService retention, RetentionPolicy baseline, ICapabilityRegistry capabilities, ICliConsole console)
    : DumpCommandBase(capabilities, console)
{
    public override Command Build()
    {
        var json = CommonOptions.Json(Console);
        var directory = new Option<string>("--directory") { Description = Console.T("Directory holding ProcLens dumps."), Required = true };
        var olderThan = new Option<int?>("--older-than-days") { Description = Console.T("Remove dumps older than this many days (overrides the policy).") };
        var keep = new Option<int?>("--keep") { Description = Console.T("Keep at most this many of the newest dumps (overrides the policy).") };
        var maxSize = new Option<long?>("--max-size-mb") { Description = Console.T("Keep the newest dumps that fit in this many MB (overrides the policy).") };
        var delete = new Option<bool>("--delete") { Description = Console.T("Actually remove the selected dumps. Without it nothing is deleted (dry run).") };

        var command = new Command("prune", Console.T("Apply the retention policy to a dump directory (Windows only). Dry run unless --delete is given."))
        {
            directory, olderThan, keep, maxSize, delete, json,
        };
        command.SetAction(async (parse, ct) =>
        {
            if (!Supported) return NotSupported();
            if (parse.GetValue(olderThan) < 0 || parse.GetValue(keep) < 0 || parse.GetValue(maxSize) < 0)
            {
                Console.ErrorLine(Console.T("Retention limits cannot be negative."));
                return ExitCodes.NotSupported;
            }

            var policy = baseline with
            {
                MaxAgeDays = parse.GetValue(olderThan) ?? baseline.MaxAgeDays,
                MaxCount = parse.GetValue(keep) ?? baseline.MaxCount,
                MaxTotalBytes = parse.GetValue(maxSize) is { } mb ? mb * 1024 * 1024 : baseline.MaxTotalBytes,
            };
            var dryRun = !parse.GetValue(delete);

            var report = await retention.RunAsync(parse.GetValue(directory)!, policy, delete: !dryRun, ct);
            foreach (var warning in report.Warnings) Console.ErrorLine(Console.T("Warning: {0}", Console.Localize(warning)));

            if (parse.GetValue(json))
            {
                Console.Line(JsonSerializer.Serialize(new { Policy = policy, Report = report }, Display.Json));
                return report.Problem is not null ? ExitCodes.NotFound : report.HasFailures ? ExitCodes.Failed : ExitCodes.Success;
            }

            if (report.Problem is not null)
            {
                Console.ErrorLine(Console.Localize(report.Problem));
                return ExitCodes.NotFound;
            }

            Console.Line(Display.PadRight(Console.T("Directory:"), 11) + report.Directory);
            Console.Line(Display.PadRight(Console.T("Policy:"), 11) +
                         Console.T("older than {0} day(s)", policy.MaxAgeDays) +
                         (policy.MaxCount is { } c ? Console.T(", keep at most {0}", c) : "") +
                         (policy.MaxTotalBytes is { } b ? Console.T(", at most {0} MB in total", b / (1024 * 1024)) : "") +
                         $"  [{policy.Source ?? Console.T("command line")}]");
            Console.Line();
            foreach (var item in report.Items)
            {
                var state = item.Status switch
                {
                    RetentionItemStatus.Deleted => Console.T("deleted"),
                    RetentionItemStatus.Failed => Console.T("FAILED"),
                    _ => Console.T("would delete"),
                };
                Console.Line($"  {Display.PadRight(state, 12)} {Display.PadLeft(Display.Bytes(item.Bytes), 10)}  {item.Created?.ToString("yyyy-MM-dd") ?? "n/a       "}  {Path.GetFileName(item.Path)}");
                Console.Line($"               {Console.Localize(item.Reason)}{(item.Error is null ? "" : "  -> " + Console.Localize(item.Error))}");
            }
            foreach (var note in report.Skipped) Console.Line($"  {Display.PadRight(Console.T("skipped"), 12)} {Console.Localize(note)}");

            Console.Line();
            Console.Line(Console.T("{0} dump(s) selected, {1} kept, {2} skipped.", report.Items.Count, report.Kept, report.Skipped.Count));
            if (dryRun)
            {
                Console.Line(report.Items.Count == 0
                    ? Console.T("Nothing to remove.")
                    : Console.T("Dry run: nothing was deleted. {0} would be freed; re-run with --delete to remove them.", Display.Bytes(report.BytesSelected)));
            }
            else
            {
                Console.Line(Console.T("Freed {0}. Each removal was recorded in the audit log.", Display.Bytes(report.BytesFreed)));
                Console.Line(Console.T("Note: unencrypted dumps were overwritten before removal, but that cannot guarantee erasure on SSDs, snapshots or backups."));
                Console.Line(Console.T("Encrypted dumps are protected by their keys: if copies exist elsewhere, they stay readable by whoever holds a key or password."));
            }
            return report.HasFailures ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }
}
