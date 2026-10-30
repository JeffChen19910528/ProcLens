using System.CommandLine;
using ProcLens.Cli.Hosting;

namespace ProcLens.Cli.Commands;

/// <summary>Options that several commands share, with their descriptions translated.</summary>
internal static class CommonOptions
{
    public static Option<bool> Json(ICliConsole console) => new("--json") { Description = console.T("Emit JSON.") };

    public static Option<int> Pid(ICliConsole console) => new("--pid") { Description = console.T("Target process id."), Required = true };
}
