using System.CommandLine;
using System.Text.Json;
using ProcLens.Application.Abstractions;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Platform;

namespace ProcLens.Cli.Commands.Dumps;

internal sealed class DumpDecryptCommand(IDumpVaultService vault, ICapabilityRegistry capabilities, ICliConsole console)
    : DumpCommandBase(capabilities, console)
{
    public override Command Build()
    {
        var file = new Option<string>("--file") { Description = Console.T("Encrypted dump (.dmp.enc)."), Required = true };
        var target = new Option<string>("--output") { Description = Console.T("New file for the decrypted dump (must not exist)."), Required = true };
        var password = new PasswordInput(Console);
        var command = new Command("decrypt", Console.T("Decrypt a dump into a plain .dmp for debugging (Windows only)."))
        {
            file, target, password.EnvironmentVariable, password.Prompt,
        };
        command.SetAction(async (parse, ct) =>
        {
            if (!Supported) return NotSupported();
            using var secret = password.Read(parse, confirm: false, out var passwordError);
            if (passwordError is not null) { Console.ErrorLine(passwordError); return ExitCodes.NotSupported; }

            return Report(await vault.DecryptAsync(parse.GetValue(file)!, parse.GetValue(target)!, secret, ct));
        });
        return command;
    }
}

internal sealed class DumpExportCommand(IDumpVaultService vault, ICapabilityRegistry capabilities, ICliConsole console)
    : DumpCommandBase(capabilities, console)
{
    public override Command Build()
    {
        var file = new Option<string>("--file") { Description = Console.T("Encrypted dump to export."), Required = true };
        var target = new Option<string>("--output") { Description = Console.T("New file for the password-protected copy (must not exist)."), Required = true };
        var newPassword = new PasswordInput(Console, "password");
        var current = new PasswordInput(Console, "current-password");
        var command = new Command("export", Console.T("Create a copy protected only by a password, so it can leave this machine (Windows only)."))
        {
            file, target, newPassword.EnvironmentVariable, newPassword.Prompt, current.EnvironmentVariable, current.Prompt,
        };
        command.SetAction(async (parse, ct) =>
        {
            if (!Supported) return NotSupported();

            using var secret = newPassword.Read(parse, confirm: true, out var passwordError);
            if (passwordError is not null) { Console.ErrorLine(passwordError); return ExitCodes.NotSupported; }
            if (secret is null)
            {
                Console.ErrorLine(Console.T("A password is required: use --password-env or --password-prompt."));
                return ExitCodes.NotSupported;
            }
            using var existing = current.Read(parse, confirm: false, out var currentError);
            if (currentError is not null) { Console.ErrorLine(currentError); return ExitCodes.NotSupported; }

            return Report(await vault.ExportAsync(parse.GetValue(file)!, parse.GetValue(target)!, secret, existing, ct));
        });
        return command;
    }
}

internal sealed class DumpVerifyCommand(IDumpVaultService vault, ICapabilityRegistry capabilities, ICliConsole console)
    : DumpCommandBase(capabilities, console)
{
    public override Command Build()
    {
        var json = CommonOptions.Json(Console);
        var file = new Option<string>("--file") { Description = Console.T("Dump file to verify."), Required = true };
        var password = new PasswordInput(Console);
        var command = new Command("verify", Console.T("Check a dump's integrity end to end without writing plaintext (Windows only)."))
        {
            file, password.EnvironmentVariable, password.Prompt, json,
        };
        command.SetAction(async (parse, ct) =>
        {
            if (!Supported) return NotSupported();
            using var secret = password.Read(parse, confirm: false, out var passwordError);
            if (passwordError is not null) { Console.ErrorLine(passwordError); return ExitCodes.NotSupported; }

            var result = await vault.VerifyAsync(parse.GetValue(file)!, secret, ct);
            if (parse.GetValue(json))
            {
                Console.Line(JsonSerializer.Serialize(result, Display.Json));
                return ExitCodes.For(result.Status);
            }
            return Report(result);
        });
        return command;
    }
}
