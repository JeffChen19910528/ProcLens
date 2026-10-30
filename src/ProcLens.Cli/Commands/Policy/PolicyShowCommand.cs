using System.CommandLine;
using System.Text.Json;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Dumps;

namespace ProcLens.Cli.Commands.Policy;

internal sealed class PolicyShowCommand(MachinePolicy policy, ICliConsole console) : ICliCommand
{
    public string Group => "policy";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var command = new Command("show", console.T("Show the machine policy ProcLens is applying, and whether the policy file is believed.")) { json };

        command.SetAction(parse =>
        {
            // Machine-readable output is always English; only the text view is translated.
            var asJson = parse.GetValue(json);
            string Say(string english) => asJson ? english : console.T(english);
            var status = policy.IgnoredBecause is not null ? Say("IGNORED")
                : !policy.Exists ? Say("absent (defaults apply)")
                : policy.Overridden ? Say("in use (path chosen with PROCLENS_POLICY)")
                : Say("in use (protected: only Administrators/SYSTEM can change it)");

            if (parse.GetValue(json))
            {
                console.Line(JsonSerializer.Serialize(new { policy.Path, policy.Overridden, policy.Exists, Status = status, policy.IgnoredBecause, policy.Admin, policy.Retention }, Display.Json));
                return ExitCodes.Success;
            }

            const int Label = 20;
            console.Line(Display.PadRight(console.T("Policy file:"), 14) + policy.Path);
            console.Line(Display.PadRight(console.T("Status:"), 14) + status);
            if (policy.IgnoredBecause is not null)
            {
                console.Line(console.T("Ignored because {0}.", console.Localize(policy.IgnoredBecause)));
                console.Line(console.T("An ordinary user could have created or edited it, so ProcLens does not believe it. See docs/SECURITY.md, \"Protecting the policy file\"."));
            }
            console.Line();
            console.Line(console.T("Administrative dumps"));
            console.Line("  " + Display.PadRight(console.T("enabled:"), Label) + policy.Admin.AdminDumpsEnabled);
            console.Line("  " + Display.PadRight(console.T("allowed operators:"), Label) +
                         (policy.Admin.AllowedOperators is { Count: > 0 } ops ? string.Join(", ", ops) : console.T("any elevated administrator")));
            console.Line("  " + Display.PadRight(console.T("reason required:"), Label) +
                         console.T("{0} (at least {1} characters)", policy.Admin.RequireReason, policy.Admin.MinReasonLength));
            console.Line(console.T("Retention"));
            console.Line("  " + Display.PadRight(console.T("max age:"), Label) + console.T("{0} day(s)", policy.Retention.MaxAgeDays));
            console.Line("  " + Display.PadRight(console.T("max count:"), Label) + (policy.Retention.MaxCount?.ToString() ?? console.T("unlimited")));
            console.Line("  " + Display.PadRight(console.T("max total size:"), Label) +
                         (policy.Retention.MaxTotalBytes is { } b ? $"{b / (1024 * 1024)} MB" : console.T("unlimited")));
            return ExitCodes.Success;
        });
        return command;
    }
}
