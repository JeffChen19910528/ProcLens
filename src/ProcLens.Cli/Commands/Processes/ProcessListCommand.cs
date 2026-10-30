using System.CommandLine;
using System.Text.Json;
using ProcLens.Application;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Processes;
using ProcLens.Cli.Hosting;

namespace ProcLens.Cli.Commands.Processes;

internal sealed class ProcessListCommand(IProcessQueryService queries, ICliConsole console) : ICliCommand
{
    public string Group => "process";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var sort = new Option<string>("--sort") { Description = console.T("Sort by pid, name, memory or cpu."), DefaultValueFactory = _ => "pid" };
        sort.AcceptOnlyFromAmong("pid", "name", "memory", "cpu");
        var name = new Option<string?>("--name") { Description = console.T("Only processes whose name contains this text.") };

        var command = new Command("list", console.T("List running processes.")) { sort, name, json };
        command.SetAction(async (parse, ct) =>
        {
            try
            {
                var key = Enum.Parse<ProcessSortKey>(parse.GetValue(sort)!, ignoreCase: true);
                var items = await queries.ListAsync(new ProcessListOptions(key, parse.GetValue(name)), ct);
                if (parse.GetValue(json))
                {
                    console.Line(JsonSerializer.Serialize(items, Display.Json));
                    return ExitCodes.Success;
                }

                console.Line($"{Display.PadLeft("PID", 8)}  {Display.PadLeft("PPID", 8)}  {Display.PadLeft("CPU", 7)}  " +
                             $"{Display.PadLeft(console.T("MEMORY"), 10)}  {Display.PadLeft(console.T("THREADS"), 7)}  {console.T("NAME")}");
                foreach (var p in items)
                    console.Line($"{p.ProcessId,8}  {p.ParentProcessId?.ToString() ?? "n/a",8}  " +
                                 $"{Display.Format(p.CpuUsage, v => $"{v:F1}%"),7}  {Display.Format(p.MemoryUsage, Display.Bytes),10}  " +
                                 $"{Display.Format(p.ThreadCount, t => t.ToString()),7}  {p.ProcessName}");
                return ExitCodes.Success;
            }
            catch (FeatureUnavailableException e)
            {
                console.ErrorLine(console.Localize(e.Message));
                return ExitCodes.NotSupported;
            }
        });
        return command;
    }
}
