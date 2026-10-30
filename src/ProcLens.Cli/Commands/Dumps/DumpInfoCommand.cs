using System.CommandLine;
using System.Text.Json;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Platform;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Cli.Commands.Dumps;

internal sealed class DumpInfoCommand(ICapabilityRegistry capabilities, ICliConsole console) : DumpCommandBase(capabilities, console)
{
    public override Command Build()
    {
        var json = CommonOptions.Json(Console);
        var file = new Option<string>("--file") { Description = Console.T("Dump file to inspect."), Required = true };
        var command = new Command("info", Console.T("Show metadata and structure of a dump file (Windows only).")) { file, json };

        command.SetAction(async (parse, ct) =>
        {
            if (!Supported) return NotSupported();

            var path = parse.GetValue(file)!;
            if (!File.Exists(path))
            {
                Console.ErrorLine(Console.T("File not found: {0}", path));
                return ExitCodes.NotFound;
            }

            var encrypted = DumpCipher.IsEncrypted(path);
            MinidumpInfo? structure = null;
            EncryptedFileInfo? header = null;
            try
            {
                if (encrypted)
                {
                    await using var stream = File.OpenRead(path);
                    header = await DumpCipher.ReadInfoAsync(stream, ct);
                }
                else
                {
                    structure = MinidumpReader.Read(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DumpDecryptionException)
            {
                Console.ErrorLine(Console.T("Cannot read the file: {0}", Console.Localize(e.Message)));
                return ExitCodes.Denied;
            }

            var sha = await DumpFiles.ComputeSha256Async(path, ct);
            var metadata = DumpFiles.TryReadMetadata(path);
            var matches = metadata is not null && string.Equals(metadata.Sha256, sha, StringComparison.OrdinalIgnoreCase);
            var asJson = parse.GetValue(json);
            string Say(string english) => asJson ? english : Console.T(english);
            var integrity = metadata is null ? Say("no metadata file")
                : matches ? Say("OK (SHA256 matches the recorded value)")
                : Say("MISMATCH (the file changed since it was recorded)");
            var healthy = (structure?.IsValid ?? true) && (metadata is null || matches);
            var exit = healthy ? ExitCodes.Success : ExitCodes.Failed;

            if (parse.GetValue(json))
            {
                Console.Line(JsonSerializer.Serialize(
                    new { Path = path, Encrypted = encrypted, Sha256 = sha, Integrity = integrity, Structure = structure, Encryption = header, Metadata = metadata }, Display.Json));
                return exit;
            }

            const int Label = 12;
            string Row(string label, string value) => Display.PadRight(label, Label) + value;

            Console.Line(Row(Console.T("File:"), path));
            Console.Line(Row(Console.T("Size:"), Display.Bytes(new FileInfo(path).Length)));
            Console.Line(Row("SHA256:", sha));
            Console.Line(Row(Console.T("Integrity:"), integrity));
            if (header is not null)
            {
                Console.Line(Row(Console.T("Encrypted:"), Console.T("yes ({0}, {1} KiB chunks)", DumpCipher.SchemeName, header.ChunkSize / 1024)));
                Console.Line(Row(Console.T("Unlocked by:"), string.Join(Console.T(" or "), header.SlotTypes.Select(SlotName))));
                Console.Line(Row(Console.T("Structure:"), Console.T("not shown while encrypted; use 'dump verify' to check it or 'dump decrypt' to open it.")));
            }
            else if (structure is not null)
            {
                Console.Line(Row(Console.T("Encrypted:"), Console.T("no")));
                Console.Line(Row(Console.T("Valid:"), structure.IsValid ? Console.T("yes") : Console.T("NO - {0}", Console.Localize(structure.Problem ?? ""))));
                if (structure.IsValid)
                {
                    Console.Line(Row(Console.T("Dump type:"), Console.T("{0} (from MINIDUMP_TYPE flags 0x{1})", structure.InferredType, $"{structure.TypeFlags:X}")));
                    Console.Line(Row(Console.T("Created:"), $"{structure.TimeStamp:u}"));
                    Console.Line(Row(Console.T("Target:"), Console.T("{0}, Windows {1}.{2} build {3}", structure.Architecture, structure.OsMajor, structure.OsMinor, structure.OsBuild)));
                    Console.Line(Row(Console.T("Threads:"), structure.ThreadCount?.ToString() ?? "n/a"));
                    Console.Line(Row(Console.T("Modules:"), structure.ModuleCount?.ToString() ?? "n/a"));
                }
            }
            if (metadata is not null)
            {
                Console.Line(Row(Console.T("Process:"), Console.T("{0} ({1}), started {2}", metadata.ProcessName, metadata.ProcessId, metadata.ProcessStartTime.ToString("u"))));
                Console.Line(Row(Console.T("Collected:"), Console.T("{0} by ProcLens {1} on {2}", metadata.CreationTime.ToString("u"), metadata.ApplicationVersion, metadata.OperatingSystem)));
            }
            return exit;
        });
        return command;
    }

    private string SlotName(byte type) => type switch
    {
        1 => Console.T("this user's machine key"),
        PasswordKekSource.Type => Console.T("a password"),
        _ => Console.T("unknown key type {0}", type),
    };
}
