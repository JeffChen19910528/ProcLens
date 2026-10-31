using System.CommandLine;
using System.Text.Json;
using ProcLens.Application;
using ProcLens.Application.Abstractions;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Models;

namespace ProcLens.Cli.Commands.Processes;

/// <summary>
/// The shape shared by <c>process threads</c> and <c>process modules</c>: fetch an <see cref="InspectionResult{T}"/>,
/// say plainly why it is empty if it is not <c>Available</c>, otherwise print a table. Subclasses only supply the data
/// and the table.
/// </summary>
internal abstract class InspectionCommand<T>(string name, string description, ICliConsole console) : ICliCommand
{
    protected ICliConsole Console => console;

    public string Group => "process";

    protected abstract Task<InspectionResult<T>> FetchAsync(int processId, CancellationToken ct);
    protected abstract void PrintTable(IReadOnlyList<T> items);

    public Command Build()
    {
        var pid = CommonOptions.Pid(console);
        var json = CommonOptions.Json(console);
        var command = new Command(name, console.T(description)) { pid, json };

        command.SetAction(async (parse, ct) =>
        {
            InspectionResult<T> result;
            try
            {
                result = await FetchAsync(parse.GetValue(pid), ct);
            }
            catch (FeatureUnavailableException e)
            {
                result = InspectionResult<T>.NotSupported(e.Reason ?? e.Message);
            }

            if (parse.GetValue(json))
            {
                console.Line(JsonSerializer.Serialize(result, Display.Json));
                return ExitCodes.For(result.Status);
            }
            if (result.Status != InspectionStatus.Available)
            {
                console.ErrorLine(Describe(result.Status, result.Reason));
                return ExitCodes.For(result.Status);
            }

            PrintTable(result.Items);
            return ExitCodes.Success;
        });
        return command;
    }

    private string Describe(InspectionStatus status, string? reason)
    {
        var why = console.Localize(reason ?? "");
        return status switch
        {
            InspectionStatus.ProcessNotFound => console.T("Process was not found."),
            InspectionStatus.NotSupported => console.T("Not supported on this platform: {0}", why),
            InspectionStatus.ArchitectureMismatch => console.T("Architecture mismatch: {0}", why),
            _ => console.T("Access denied: {0}", why),
        };
    }
}

internal sealed class ProcessThreadsCommand(IProcessQueryService queries, ICliConsole console)
    : InspectionCommand<ThreadInfo>("threads", "List the threads of one process.", console)
{
    protected override Task<InspectionResult<ThreadInfo>> FetchAsync(int processId, CancellationToken ct) => queries.GetThreadsAsync(processId, ct);

    protected override void PrintTable(IReadOnlyList<ThreadInfo> threads)
    {
        Console.Line($"{Display.PadLeft("TID", 8)}  {Display.PadRight(Console.T("STATE"), 9)}  {Display.PadLeft(Console.T("CPU TIME"), 12)}  {Console.T("NAME / DETAIL")}");
        foreach (var t in threads)
            Console.Line($"{t.ThreadId,8}  {Display.PadRight(t.State.ToString(), 9)}  {Display.PadLeft(Display.Format(t.CpuTime, Display.Seconds), 12)}  " +
                         string.Join("  ", new[] { t.Name, t.StateDetail }.Where(x => x is not null)));
        Console.Line(Console.T("{0} thread(s).", threads.Count));
    }
}

internal sealed class ProcessModulesCommand(IProcessQueryService queries, ICliConsole console)
    : InspectionCommand<ModuleInfo>("modules", "List the loaded modules of one process.", console)
{
    protected override Task<InspectionResult<ModuleInfo>> FetchAsync(int processId, CancellationToken ct) => queries.GetModulesAsync(processId, ct);

    protected override void PrintTable(IReadOnlyList<ModuleInfo> modules)
    {
        Console.Line($"{Display.PadLeft(Console.T("BASE"), 18)}  {Display.PadLeft(Console.T("SIZE"), 10)}  {Console.T("NAME")}  {Console.T("PATH")}");
        foreach (var m in modules)
            Console.Line($"{Display.PadLeft(Display.Format(m.BaseAddress, a => $"0x{a:X}"), 18)}  {Display.PadLeft(Display.Format(m.Size, Display.Bytes), 10)}  {m.Name}  {m.Path ?? "n/a"}");
        Console.Line(Console.T("{0} module(s).", modules.Count));
    }
}
