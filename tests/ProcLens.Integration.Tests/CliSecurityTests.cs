using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;

namespace ProcLens.Integration.Tests;

[Collection("CLI")]
public class CliSecurityTests : CliTestBase
{
    private const string Password = "Tr0ub4dor&3-correct-horse";

    private static Process StartTarget()
    {
        var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-t 127.0.0.1")
        { CreateNoWindow = true, UseShellExecute = false })!;
        Thread.Sleep(500);
        return process;
    }

    private string DumpDirectory => Path.Combine(Root, "dumps");

    private string[] AuditOperations() =>
        File.ReadAllLines(AuditFile).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("Operation").GetString()!).ToArray();

    // ---------- every platform ----------

    [Fact]
    public async Task Audit_commands_work_on_an_empty_log()
    {
        var verify = await Run("audit", "verify");
        var show = await Run("audit", "show");

        verify.Code.Should().Be(0);
        verify.Out.Should().Contain("0 record(s)");
        show.Code.Should().Be(0);
        show.Out.Should().Contain("No audit records");
    }

    [Fact]
    public async Task Off_windows_the_protection_commands_are_rejected_and_nothing_is_audited_or_created()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var args in new[]
        {
            new[] { "dump", "decrypt", "--file", "a.enc", "--output", "b.dmp" },
            new[] { "dump", "export", "--file", "a.enc", "--output", "b.enc", "--password-env", PasswordVariable },
            new[] { "dump", "verify", "--file", "a.enc" },
        })
        {
            var (code, _, err) = await Run(args);
            code.Should().Be(2, string.Join(' ', args));
            err.Should().Contain("Process dump is not supported on this operating system.");
        }

        File.Exists(AuditFile).Should().BeFalse();
    }

    [Fact]
    public async Task A_password_variable_that_is_not_set_is_a_clear_error()
    {
        var (code, _, err) = await Run("dump", "create", "--pid", "1", "--output", DumpDirectory, "--password-env", "PROCLENS_NOT_SET_ANYWHERE");

        code.Should().Be(2);
        err.Should().Contain("PROCLENS_NOT_SET_ANYWHERE");
    }

    // ---------- Windows end to end ----------

    [Fact]
    public async Task Encrypted_by_default_then_verify_decrypt_and_audit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var target = StartTarget();
        try
        {
            var create = await Run("dump", "create", "--pid", target.Id.ToString(), "--type", "heap", "--output", DumpDirectory, "--json");
            create.Code.Should().Be(0, create.Err);
            using var created = JsonDocument.Parse(create.Out);
            var file = created.RootElement.GetProperty("FilePath").GetString()!;
            file.Should().EndWith(".dmp.enc");
            Directory.GetFiles(DumpDirectory).Should().HaveCount(2, "no plaintext sibling");

            var info = await Run("dump", "info", "--file", file);
            info.Code.Should().Be(0, info.Err);
            info.Out.Should().Contain("Encrypted:  yes").And.Contain("Integrity:  OK").And.Contain("machine key");

            (await Run("dump", "verify", "--file", file)).Code.Should().Be(0);

            var plain = Path.Combine(Root, "plain.dmp");
            var decrypt = await Run("dump", "decrypt", "--file", file, "--output", plain);
            decrypt.Code.Should().Be(0, decrypt.Err);
            (await Run("dump", "info", "--file", plain)).Out.Should().Contain("Valid:      yes").And.Contain("Dump type:  Heap");
            (await Run("dump", "decrypt", "--file", file, "--output", plain)).Code.Should().Be(2, "never overwrite");

            AuditOperations().Should().ContainInOrder("dump.create", "dump.verify", "dump.decrypt");
            var audit = await Run("audit", "verify");
            audit.Code.Should().Be(0, audit.Err);
        }
        finally { target.Kill(); }
    }

    [Fact]
    public async Task Password_export_round_trip_and_the_password_never_appears_anywhere()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var target = StartTarget();
        try
        {
            Environment.SetEnvironmentVariable(PasswordVariable, Password);
            var create = await Run("dump", "create", "--pid", target.Id.ToString(), "--output", DumpDirectory, "--password-env", PasswordVariable, "--json");
            create.Code.Should().Be(0, create.Err);
            var file = JsonDocument.Parse(create.Out).RootElement.GetProperty("FilePath").GetString()!;

            var exported = Path.Combine(Root, "export.dmp.enc");
            var export = await Run("dump", "export", "--file", file, "--output", exported, "--password-env", PasswordVariable);
            export.Code.Should().Be(0, export.Err);

            (await Run("dump", "verify", "--file", exported, "--password-env", PasswordVariable)).Code.Should().Be(0);

            Environment.SetEnvironmentVariable(PasswordVariable, "a different but long password");
            (await Run("dump", "verify", "--file", exported, "--password-env", PasswordVariable)).Code.Should().Be(3);
            Environment.SetEnvironmentVariable(PasswordVariable, null);
            (await Run("dump", "verify", "--file", exported)).Code.Should().Be(3, "the export carries no machine key");

            var everything = string.Join("\n", create.Out, create.Err, export.Out, export.Err,
                File.ReadAllText(AuditFile),
                string.Join("\n", Directory.GetFiles(Root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText)));
            everything.Should().NotContain(Password).And.NotContain("Tr0ub4dor");
        }
        finally { target.Kill(); }
    }

    [Fact]
    public async Task A_weak_password_is_rejected_and_no_file_is_created()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var target = StartTarget();
        try
        {
            Environment.SetEnvironmentVariable(PasswordVariable, "short");

            var result = await Run("dump", "create", "--pid", target.Id.ToString(), "--output", DumpDirectory, "--password-env", PasswordVariable);

            result.Code.Should().Be(2);
            result.Err.Should().Contain("at least 12");
            (Directory.Exists(DumpDirectory) ? Directory.GetFiles(DumpDirectory) : []).Should().BeEmpty();
        }
        finally { target.Kill(); }
    }

    [Fact]
    public async Task Administrative_requests_are_denied_by_default_and_the_attempt_and_justification_are_audited()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A process whose owner an ordinary user cannot read, so ownership cannot justify the request.
        var list = await Run("process", "list", "--json");
        using var processes = JsonDocument.Parse(list.Out);
        var otherPid = processes.RootElement.EnumerateArray()
            .Where(p => p.GetProperty("ProcessId").GetInt32() is var id && id > 4 && id != Environment.ProcessId)
            .Where(p => p.GetProperty("Owner").ValueKind == JsonValueKind.Null && p.GetProperty("StartTime").ValueKind != JsonValueKind.Null)
            .Select(p => (int?)p.GetProperty("ProcessId").GetInt32())
            .FirstOrDefault();
        if (otherPid is null) return; // e.g. running fully elevated: every owner is readable, nothing to assert

        var plain = await Run("dump", "create", "--pid", otherPid.Value.ToString(), "--output", DumpDirectory);
        var admin = await Run("dump", "create", "--pid", otherPid.Value.ToString(), "--output", DumpDirectory,
            "--authorize-admin", "--reason", "investigating a hang reported by the service desk");

        plain.Code.Should().Be(3);
        admin.Code.Should().Be(3, "elevation and an enabling machine policy are both still required");
        admin.Err.Should().MatchRegex("elevated|disabled by machine policy");
        (Directory.Exists(DumpDirectory) ? Directory.GetFiles(DumpDirectory) : []).Should().BeEmpty();

        var records = File.ReadAllLines(AuditFile).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        records.Should().HaveCount(2).And.OnlyContain(r => r.GetProperty("Outcome").GetString() == "Denied");
        records[1].GetProperty("Justification").GetString().Should().Contain("service desk");
        records[1].GetProperty("Authorization").GetProperty("Allowed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Editing_the_audit_log_is_detected_by_the_audit_verify_command()
    {
        if (!OperatingSystem.IsWindows()) return;

        (await Run("dump", "create", "--pid", int.MaxValue.ToString(), "--output", DumpDirectory)).Code.Should().Be(1);
        (await Run("dump", "create", "--pid", (int.MaxValue - 1).ToString(), "--output", DumpDirectory)).Code.Should().Be(1);
        (await Run("audit", "verify")).Code.Should().Be(0);

        var lines = File.ReadAllLines(AuditFile);
        lines[0] = lines[0].Replace("\"Outcome\":\"Failed\"", "\"Outcome\":\"Succeeded\"");
        File.WriteAllLines(AuditFile, lines);

        var tampered = await Run("audit", "verify");

        tampered.Code.Should().Be(5);
        tampered.Err.Should().Contain("TAMPERED").And.Contain("line 1");
    }

    [Fact]
    public async Task The_audit_file_and_key_directory_are_private_to_the_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var target = StartTarget();
        try
        {
            (await Run("dump", "create", "--pid", target.Id.ToString(), "--output", DumpDirectory)).Code.Should().Be(0);

            foreach (var path in new[] { AuditFile, Path.Combine(Root, "keys", "master.key") })
            {
                var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                rules.Count.Should().Be(1, path);
            }
        }
        finally { target.Kill(); }
    }

    // ---------- retention ----------

    private string CreateManagedDump(double daysOld)
    {
        Directory.CreateDirectory(DumpDirectory);
        var path = Path.Combine(DumpDirectory, $"demo_{Guid.NewGuid():N}.dmp.enc");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var created = DateTimeOffset.UtcNow.AddDays(-daysOld);
        File.WriteAllText(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(new DumpMetadata(
            Guid.NewGuid(), 42, "demo", created.AddMinutes(-1), DumpType.Mini, path, 4, created, "OS", "1",
            CollectionStatus.Succeeded, null, "stored", "plain", "scheme")));
        return path;
    }

    [Fact]
    public async Task Prune_is_rejected_off_windows_and_never_touches_the_directory()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(DumpDirectory);

        var (code, _, err) = await Run("dump", "prune", "--directory", DumpDirectory, "--delete");

        code.Should().Be(2);
        err.Should().Contain("Process dump is not supported on this operating system.");
        File.Exists(AuditFile).Should().BeFalse();
    }

    [Fact]
    public async Task Prune_defaults_to_a_dry_run_and_only_deletes_when_asked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = CreateManagedDump(60);
        var recent = CreateManagedDump(2);

        var dry = await Run("dump", "prune", "--directory", DumpDirectory);
        dry.Code.Should().Be(0, dry.Err);
        dry.Out.Should().Contain("would delete").And.Contain("Dry run: nothing was deleted").And.Contain("--delete");
        File.Exists(old).Should().BeTrue();
        File.Exists(AuditFile).Should().BeFalse("a dry run changes nothing, so it records nothing");

        var real = await Run("dump", "prune", "--directory", DumpDirectory, "--delete");
        real.Code.Should().Be(0, real.Err);
        real.Out.Should().Contain("deleted").And.Contain("Each removal was recorded");
        File.Exists(old).Should().BeFalse();
        File.Exists(DumpFiles.SidecarPath(old)).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();

        AuditOperations().Should().Equal("dump.delete");
        (await Run("audit", "verify")).Code.Should().Be(0);
    }

    [Fact]
    public async Task Prune_reads_the_policy_file_and_command_line_options_override_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        CreateManagedDump(3);
        CreateManagedDump(2);
        CreateManagedDump(1);
        File.WriteAllText(PolicyFile, "{ \"Retention\": { \"MaxAgeDays\": 365, \"MaxCount\": 1 } }");

        var fromPolicy = await Run("dump", "prune", "--directory", DumpDirectory, "--json");
        using (var doc = JsonDocument.Parse(fromPolicy.Out))
            doc.RootElement.GetProperty("Report").GetProperty("Items").GetArrayLength().Should().Be(2);

        var overridden = await Run("dump", "prune", "--directory", DumpDirectory, "--keep", "3", "--json");
        using (var doc = JsonDocument.Parse(overridden.Out))
            doc.RootElement.GetProperty("Report").GetProperty("Items").GetArrayLength().Should().Be(0);

        Directory.GetFiles(DumpDirectory, "*.dmp.enc").Should().HaveCount(3, "both runs were dry runs");
    }

    [Fact]
    public async Task Prune_reports_bad_input_with_distinct_exit_codes()
    {
        if (!OperatingSystem.IsWindows()) return;

        (await Run("dump", "prune", "--directory", Path.Combine(Root, "nope"))).Code.Should().Be(1);
        (await Run("dump", "prune", "--directory", Root, "--keep", "-1")).Code.Should().Be(2);
    }

    [Fact]
    public async Task Prune_refuses_to_delete_when_the_audit_log_is_unwritable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = CreateManagedDump(60);
        Environment.SetEnvironmentVariable(ProcLens.Core.Dumps.ProcLensPaths.AuditVariable, Path.Combine(old, "cannot", "audit.jsonl")); // parent is a file

        var (code, _, err) = await Run("dump", "prune", "--directory", DumpDirectory, "--delete");

        code.Should().Be(1);
        err.Should().Contain("audit log is not writable");
        File.Exists(old).Should().BeTrue();
    }
}
