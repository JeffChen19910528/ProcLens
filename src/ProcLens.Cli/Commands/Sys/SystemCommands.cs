using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Platform;

namespace ProcLens.Cli.Commands.Sys;

internal sealed class SystemInfoCommand(IPlatformDetector detector, ICliConsole console) : ICliCommand
{
    public string Group => "system";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var command = new Command("info", console.T("Show operating system and runtime information.")) { json };
        command.SetAction(parse =>
        {
            var data = new
            {
                Platform = detector.Current.ToString(),
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                LogicalProcessors = Environment.ProcessorCount,
                Runtime = RuntimeInformation.FrameworkDescription,
                ApplicationVersion = typeof(SystemInfoCommand).Assembly.GetName().Version?.ToString(),
            };
            if (parse.GetValue(json)) console.Line(JsonSerializer.Serialize(data, Display.Json));
            else
                foreach (var p in data.GetType().GetProperties())
                    console.Line($"{p.Name}: {p.GetValue(data)}");
            return ExitCodes.Success;
        });
        return command;
    }
}

internal sealed class SystemCapabilitiesCommand(ICapabilityRegistry registry, ICliConsole console) : ICliCommand
{
    public string Group => "system";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var command = new Command("capabilities", console.T("Show which features this platform supports.")) { json };
        command.SetAction(parse =>
        {
            if (parse.GetValue(json))
            {
                console.Line(JsonSerializer.Serialize(new { Platform = registry.Platform, Capabilities = registry.All }, Display.Json));
                return ExitCodes.Success;
            }

            console.Line($"{registry.Platform}:");
            foreach (var c in registry.All)
            {
                var status = c.Status == SupportStatus.Supported ? console.T("Supported") : console.T("Not Supported");
                console.Line($"- {c.Capability}: {status}" + (c.Reason is null ? "" : $" ({console.Localize(c.Reason)})"));
            }
            return ExitCodes.Success;
        });
        return command;
    }
}
