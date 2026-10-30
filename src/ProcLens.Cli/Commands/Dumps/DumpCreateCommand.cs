using System.CommandLine;
using System.Text.Json;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Dumps;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Platform;

namespace ProcLens.Cli.Commands.Dumps;

internal sealed class DumpCreateCommand(IDumpService dumps, ICapabilityRegistry capabilities, ICliConsole console)
    : DumpCommandBase(capabilities, console)
{
    public override Command Build()
    {
        var json = CommonOptions.Json(Console);
        var pid = CommonOptions.Pid(Console);
        var type = new Option<string>("--type") { Description = Console.T("mini, heap or full."), DefaultValueFactory = _ => "mini" };
        type.AcceptOnlyFromAmong("mini", "heap", "full");
        var outputDir = new Option<string>("--output") { Description = Console.T("Directory to write the dump into."), Required = true };
        var progress = new Option<bool>("--progress") { Description = Console.T("Report bytes written on stderr.") };
        var noEncrypt = new Option<bool>("--no-encrypt")
        {
            Description = Console.T("Store the dump unencrypted. Dumps are encrypted at rest by default; only use this when you will protect the file yourself."),
        };
        var password = new PasswordInput(Console);
        var admin = new Option<bool>("--authorize-admin")
        {
            Description = Console.T("Explicitly authorize dumping a process owned by another user (needs an elevated session and an enabling machine policy)."),
        };
        var reason = new Option<string?>("--reason") { Description = Console.T("Justification for administrative access; recorded in the audit log.") };

        var command = new Command("create", Console.T("Create a process dump (Windows only)."))
        {
            pid, type, outputDir, progress, noEncrypt, password.EnvironmentVariable, password.Prompt, admin, reason, json,
        };
        command.SetAction(async (parse, ct) =>
        {
            var dumpType = Enum.Parse<DumpType>(parse.GetValue(type)!, ignoreCase: true);

            using var secret = password.Read(parse, confirm: true, out var passwordError);
            if (passwordError is not null) { Console.ErrorLine(passwordError); return ExitCodes.NotSupported; }

            IProgress<DumpProgress>? reporter = parse.GetValue(progress)
                ? new Progress<DumpProgress>(p => Console.ErrorLine("  " + Console.T("written: {0}", Display.Bytes(p.BytesWritten))))
                : null;
            var authorization = parse.GetValue(admin) ? new AdminAuthorization(parse.GetValue(reason) ?? "") : null;

            var result = await dumps.CreateAsync(new DumpCreateOptions(
                parse.GetValue(pid), dumpType, parse.GetValue(outputDir)!,
                Encrypt: !parse.GetValue(noEncrypt), Password: secret, Admin: authorization, Progress: reporter), ct);

            foreach (var warning in result.Warnings ?? []) Console.ErrorLine(Console.T("Warning: {0}", Console.Localize(warning)));

            if (parse.GetValue(json))
            {
                Console.Line(JsonSerializer.Serialize(result, Display.Json));
                return ExitCodes.For(result.Status);
            }

            if (result.Status != DumpStatus.Succeeded)
            {
                Console.ErrorLine(Console.Localize(result.Message ?? ""));
                return ExitCodes.For(result.Status);
            }

            var m = result.Metadata!;
            const int Label = 12;
            Console.Line(Console.T("Dump created: {0}", result.FilePath));
            Console.Line("  " + Display.PadRight(Console.T("Type:"), Label) + m.DumpType);
            Console.Line("  " + Display.PadRight(Console.T("Size:"), Label) + Display.Bytes(m.FileSize));
            Console.Line("  " + Display.PadRight("SHA256:", Label) + m.Sha256);
            Console.Line("  " + Display.PadRight(Console.T("Process:"), Label) + Console.T("{0} ({1}), started {2}", m.ProcessName, m.ProcessId, m.ProcessStartTime.ToString("u")));
            Console.Line("  " + Display.PadRight(Console.T("Protected:"), Label) +
                         (m.EncryptionScheme is null ? Console.T("NO - stored unencrypted") : Console.T("encrypted ({0})", m.EncryptionScheme)));
            if (result.Authorization is { Policy: "admin" })
                Console.Line("  " + Display.PadRight(Console.T("Authorized:"), Label) + Console.T("administrative (recorded in the audit log)"));
            Console.Line();
            Console.Line(Console.T("This file may contain passwords, tokens, keys and personal data from the process's memory."));
            Console.Line(Console.T("Keep it private, do not upload it, and delete it securely when you no longer need it."));
            if (m.EncryptionScheme is not null) Console.Line(Console.T("Use 'dump decrypt' to turn it back into a debuggable .dmp."));
            return ExitCodes.Success;
        });
        return command;
    }
}
