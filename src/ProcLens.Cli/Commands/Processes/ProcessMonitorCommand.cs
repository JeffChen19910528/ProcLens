using System.CommandLine;
using System.Text.Json;
using ProcLens.Application;
using ProcLens.Application.Abstractions;
using ProcLens.Cli.Hosting;

namespace ProcLens.Cli.Commands.Processes;

internal sealed class ProcessMonitorCommand(IProcessMonitor monitor, ICliConsole console) : ICliCommand
{
    public string Group => "process";

    public Command Build()
    {
        var pid = CommonOptions.Pid(console);
        var json = CommonOptions.Json(console);
        var interval = new Option<double>("--interval")
        {
            Description = console.T("Seconds between samples (minimum 0.1)."),
            DefaultValueFactory = _ => 2.0,
        };
        interval.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<double>() < 0.1) r.AddError(console.T("--interval must be at least 0.1 seconds."));
        });
        var count = new Option<int?>("--count") { Description = console.T("Stop after this many samples (default: until the process exits or Ctrl+C).") };

        var command = new Command("monitor", console.T("Sample CPU and memory of one process over time.")) { pid, interval, count, json };
        command.SetAction(async (parse, ct) =>
        {
            var id = parse.GetValue(pid);
            var asJson = parse.GetValue(json);
            var max = parse.GetValue(count);
            var samples = 0;

            if (!asJson)
                console.Line($"{Display.PadLeft(console.T("TIME"), 8)}  {Display.PadLeft("CPU%", 6)}  {Display.PadLeft("CORE%", 7)}  {Display.PadLeft("AVG%", 6)}  " +
                             $"{Display.PadLeft(console.T("WORKSET"), 10)}  {Display.PadLeft(console.T("PRIVATE"), 10)}  {Display.PadLeft(console.T("VIRTUAL"), 10)}  {Display.PadLeft(console.T("PEAK"), 10)}");
            try
            {
                await foreach (var s in monitor.MonitorAsync(id, TimeSpan.FromSeconds(parse.GetValue(interval)), maxSamples: max, ct: ct))
                {
                    samples++;
                    if (asJson)
                    {
                        console.Line(JsonSerializer.Serialize(s, Display.JsonLine));
                        continue;
                    }

                    console.Line($"{s.Timestamp:HH:mm:ss}  {Display.Format(s.Process.CpuUsage, v => $"{v:F1}"),6}  " +
                                 $"{Display.Format(s.CpuPercentOfOneCore, v => $"{v:F1}"),7}  {Display.Format(s.AverageCpuPercentOfMachine, v => $"{v:F1}"),6}  " +
                                 $"{Display.Format(s.Memory.WorkingSet, Display.Bytes),10}  {Display.Format(s.Memory.PrivateMemory, Display.Bytes),10}  " +
                                 $"{Display.Format(s.Memory.VirtualMemory, Display.Bytes),10}  {Display.Format(s.Memory.PeakWorkingSet, Display.Bytes),10}");
                    foreach (var w in s.Warnings) console.Line($"          ! {w.Code}: {console.Localize(w.Message)}");
                }
            }
            catch (FeatureUnavailableException e)
            {
                console.ErrorLine(console.Localize(e.Message));
                return ExitCodes.NotSupported;
            }
            catch (OperationCanceledException)
            {
                return ExitCodes.Cancelled;
            }

            if (samples == 0)
            {
                console.ErrorLine(console.T("Process {0} was not found.", id));
                return ExitCodes.NotFound;
            }
            if (max is null || samples < max) console.ErrorLine(console.T("Process {0} exited; monitoring stopped.", id));
            return ExitCodes.Success;
        });
        return command;
    }
}
