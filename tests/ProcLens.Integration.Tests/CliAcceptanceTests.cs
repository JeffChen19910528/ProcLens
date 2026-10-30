using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace ProcLens.Integration.Tests;

/// <summary>Phase 6 acceptance criteria that are best shown end to end against a real process.</summary>
[Collection("CLI")]
public class CliAcceptanceTests : CliTestBase
{
    private string DumpDirectory => Path.Combine(Root, "dumps");

    private static bool Contains(string path, string marker)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(marker)) >= 0
            || bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(marker)) >= 0;
    }

    [Fact]
    public async Task Memory_contents_reach_the_dump_but_never_any_output_audit_record_or_metadata_and_not_the_ciphertext()
    {
        if (!OperatingSystem.IsWindows()) return;

        var marker = "PROCLENS_MEMORY_MARKER_" + Guid.NewGuid().ToString("N");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var target = Process.Start(new ProcessStartInfo(powershell, $"-NoProfile -Command \"$secret = '{marker}'; Start-Sleep 120\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            await Task.Delay(4000);

            // Positive control: an unencrypted heap dump must contain the marker, otherwise the checks below prove nothing.
            var plain = await Run("dump", "create", "--pid", target.Id.ToString(), "--type", "heap", "--output", DumpDirectory, "--no-encrypt", "--json");
            plain.Code.Should().Be(0, plain.Err);
            var plainFile = JsonDocument.Parse(plain.Out).RootElement.GetProperty("FilePath").GetString()!;
            Contains(plainFile, marker).Should().BeTrue("the control dump has to contain the marker for this test to mean anything");

            var encrypted = await Run("dump", "create", "--pid", target.Id.ToString(), "--type", "heap", "--output", DumpDirectory, "--json");
            encrypted.Code.Should().Be(0, encrypted.Err);
            var encryptedFile = JsonDocument.Parse(encrypted.Out).RootElement.GetProperty("FilePath").GetString()!;
            Contains(encryptedFile, marker).Should().BeFalse("the marker was in memory when the encrypted dump was taken, yet it must not be visible in it");

            var info = await Run("dump", "info", "--file", encryptedFile);
            var verify = await Run("dump", "verify", "--file", encryptedFile);
            var prune = await Run("dump", "prune", "--directory", DumpDirectory, "--older-than-days", "0", "--delete");
            var audit = await Run("audit", "show", "--last", "50");

            var sidecars = Directory.Exists(Root)
                ? string.Join("\n", Directory.GetFiles(Root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText))
                : "";
            var everythingElse = string.Join("\n",
                plain.Out, plain.Err, encrypted.Out, encrypted.Err, info.Out, info.Err, verify.Out, verify.Err,
                prune.Out, prune.Err, audit.Out, audit.Err, File.ReadAllText(AuditFile), sidecars);
            everythingElse.Should().NotContain(marker);
        }
        finally { target.Kill(); }
    }
}
