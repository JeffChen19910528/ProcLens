using ProcLens.Application.Dumps;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Platform;

namespace ProcLens.Cli.Commands.Dumps;

/// <summary>What every <c>dump</c> subcommand shares: the platform check, the exact not-supported message, and vault result printing.</summary>
internal abstract class DumpCommandBase(ICapabilityRegistry capabilities, ICliConsole console) : ICliCommand
{
    protected ICliConsole Console { get; } = console;

    public string Group => "dump";

    public abstract System.CommandLine.Command Build();

    /// <summary>The text the specification requires verbatim on platforms without dump support.</summary>
    public const string NotSupportedMessage = "Process dump is not supported on this operating system.\nSupported platform: Windows.";

    protected bool Supported => capabilities.IsSupported(Capability.ProcessDump);

    /// <summary>Prints the not-supported message and returns its exit code.</summary>
    protected int NotSupported()
    {
        Console.ErrorLine(Console.T(NotSupportedMessage));
        return ExitCodes.NotSupported;
    }

    /// <summary>Prints a vault outcome (success to stdout, problems to stderr) and returns the matching exit code.</summary>
    protected int Report(VaultResult result)
    {
        var writer = result.Status == VaultStatus.Succeeded ? Console.Out : Console.Error;
        writer.WriteLine(Console.Localize(result.Message));
        if (result.OutputPath is not null && result.Status == VaultStatus.Succeeded) Console.Line("  " + Console.T("Output: {0}", result.OutputPath));
        foreach (var detail in result.Details ?? []) writer.WriteLine("  " + Console.Localize(detail));
        return ExitCodes.For(result.Status);
    }
}
