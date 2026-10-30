using System.CommandLine;
using System.Text.Json;
using ProcLens.Application.Abstractions;
using ProcLens.Cli.Hosting;

namespace ProcLens.Cli.Commands.Processes;

internal sealed class ProcessInspectCommand(IProcessQueryService queries, ICliConsole console) : ICliCommand
{
    public string Group => "process";

    public Command Build()
    {
        var pid = CommonOptions.Pid(console);
        var json = CommonOptions.Json(console);
        var command = new Command("inspect", console.T("Show details for one process.")) { pid, json };

        command.SetAction(async (parse, ct) =>
        {
            var id = parse.GetValue(pid);
            var info = await queries.InspectAsync(id, ct);
            if (info is null)
            {
                console.ErrorLine(console.T("Process {0} was not found.", id));
                return ExitCodes.NotFound;
            }

            if (parse.GetValue(json))
            {
                console.Line(JsonSerializer.Serialize(info, Display.Json));
                return ExitCodes.Success;
            }

            var rows = new (string Label, string Value)[]
            {
                (console.T("PID:"), info.ProcessId.ToString()),
                (console.T("Name:"), info.ProcessName),
                (console.T("Executable:"), info.ExecutablePath ?? "n/a"),
                (console.T("Parent PID:"), info.ParentProcessId?.ToString() ?? "n/a"),
                (console.T("Owner:"), info.Owner ?? "n/a"),
                (console.T("Start time:"), info.StartTime?.ToString("u") ?? "n/a"),
                (console.T("Memory:"), Display.Format(info.MemoryUsage, Display.Bytes)),
                (console.T("Threads:"), Display.Format(info.ThreadCount, t => t.ToString())),
                (console.T("CPU time:"), Display.Format(info.CpuTime, Display.Seconds)),
                (console.T("Access:"), info.AccessStatus.ToString()),
            };
            var width = Math.Max(16, rows.Max(r => Display.Width(r.Label) + 1));
            foreach (var (label, value) in rows) console.Line(Display.PadRight(label, width) + value);
            return ExitCodes.Success;
        });
        return command;
    }
}
