using FluentAssertions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Security;

namespace ProcLens.Core.Tests;

public class AdminAwareDumpAuthorizerTests
{
    private sealed class Op(bool elevated, string identity = @"PC\alice") : IOperatorContext
    {
        public string Identity => identity;
        public bool IsElevated => elevated;
        public string Machine => "PC";
    }

    private static readonly AdminAuthorization Admin = new("investigating crash of the billing service");

    private static AdminDumpPolicy Enabled(IReadOnlyList<string>? allowed = null, bool requireReason = true) =>
        new(AdminDumpsEnabled: true, AllowedOperators: allowed, RequireReason: requireReason, Source: "test-policy");

    private static AuthorizationDecision Decide(string? owner, AdminAuthorization? admin, bool elevated, AdminDumpPolicy policy) =>
        new AdminAwareDumpAuthorizer(new Op(elevated), policy).Authorize(new DumpAuthorizationContext(7, owner, @"PC\alice", admin));

    [Fact]
    public void The_operators_own_process_never_needs_administrative_authorization()
    {
        var d = Decide(@"PC\alice", null, elevated: false, AdminDumpPolicy.Disabled());

        d.Allowed.Should().BeTrue();
        d.Policy.Should().Be("owner");
    }

    [Fact]
    public void Someone_elses_process_is_denied_by_default_and_the_message_explains_how_to_ask()
    {
        var d = Decide(@"PC\bob", null, elevated: true, Enabled());

        d.Allowed.Should().BeFalse();
        d.Policy.Should().Be("denied");
        d.Reason.Should().Contain(@"PC\bob").And.Contain("--authorize-admin").And.Contain("--reason");
    }

    [Fact]
    public void An_explicit_request_alone_is_not_enough_without_elevation()
    {
        var d = Decide(@"PC\bob", Admin, elevated: false, Enabled());

        d.Allowed.Should().BeFalse();
        d.Reason.Should().Contain("elevated").And.Contain("never elevates itself");
    }

    [Fact]
    public void Elevation_and_a_request_are_not_enough_while_the_machine_policy_is_disabled()
    {
        var d = Decide(@"PC\bob", Admin, elevated: true, AdminDumpPolicy.Disabled("no policy file"));

        d.Allowed.Should().BeFalse();
        d.Reason.Should().Contain("disabled by machine policy");
    }

    [Fact]
    public void The_operator_must_be_on_the_allow_list_when_there_is_one()
    {
        Decide(@"PC\bob", Admin, true, Enabled(["PC\\carol"])).Allowed.Should().BeFalse();
        Decide(@"PC\bob", Admin, true, Enabled(["pc\\ALICE"])).Allowed.Should().BeTrue("matching is case-insensitive");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too short")]
    public void A_missing_or_trivial_justification_is_refused_when_one_is_required(string reason)
    {
        var d = Decide(@"PC\bob", new AdminAuthorization(reason), true, Enabled());

        d.Allowed.Should().BeFalse();
        d.Reason.Should().Contain("justification");
    }

    [Fact]
    public void When_every_condition_holds_the_request_is_allowed_and_identified_as_administrative()
    {
        var d = Decide(@"PC\bob", Admin, true, Enabled());

        d.Allowed.Should().BeTrue();
        d.Policy.Should().Be("admin");
        d.Reason.Should().Contain(@"PC\alice").And.Contain(@"PC\bob");
    }

    [Fact]
    public void A_justification_can_be_made_optional_by_policy()
    {
        Decide(@"PC\bob", new AdminAuthorization(""), true, Enabled(requireReason: false)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_owner_is_denied_without_admin_authorization_but_can_be_authorized_explicitly()
    {
        Decide(null, null, true, Enabled()).Allowed.Should().BeFalse();
        Decide(null, Admin, true, Enabled()).Policy.Should().Be("admin");
    }
}

public class AdminDumpPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-pol-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_missing_policy_file_means_disabled() =>
        AdminDumpPolicy.Load(Path.Combine(_dir, "nope.json")).AdminDumpsEnabled.Should().BeFalse();

    [Fact]
    public void An_explicit_policy_enables_and_restricts()
    {
        var policy = AdminDumpPolicy.Load(Write("""
            { "AdminDumpsEnabled": true, "AllowedOperators": ["CORP\\ops"], "RequireReason": false, "MinReasonLength": 5 }
            """));

        policy.AdminDumpsEnabled.Should().BeTrue();
        policy.AllowedOperators.Should().Equal(@"CORP\ops");
        policy.RequireReason.Should().BeFalse();
        policy.MinReasonLength.Should().Be(5);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public void An_unreadable_or_invalid_policy_fails_closed(string content)
    {
        var policy = AdminDumpPolicy.Load(Write(content));

        policy.AdminDumpsEnabled.Should().BeFalse();
        policy.Source.Should().NotBeNull();
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_in_the_policy_file()
    {
        AdminDumpPolicy.Load(Write("{ // allow\n \"AdminDumpsEnabled\": true, }")).AdminDumpsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Locations_can_be_overridden_by_environment_variables()
    {
        var old = Environment.GetEnvironmentVariable(ProcLensPaths.AuditVariable);
        try
        {
            Environment.SetEnvironmentVariable(ProcLensPaths.AuditVariable, Path.Combine(_dir, "custom.jsonl"));
            ProcLensPaths.AuditLog.Should().Be(Path.Combine(_dir, "custom.jsonl"));
        }
        finally { Environment.SetEnvironmentVariable(ProcLensPaths.AuditVariable, old); }
    }
}

public class AuditChainTests
{
    private static AuditRecord Record(string operation = "dump.create", AuditOutcome outcome = AuditOutcome.Succeeded) => new(
        Guid.NewGuid(), DateTimeOffset.Parse("2026-01-02T03:04:05Z"), @"PC\alice", false, "PC", operation, 42, "demo",
        DateTimeOffset.Parse("2026-01-02T03:00:00Z"), null, "Mini", @"C:\dumps", new AuditAuthorization(true, "owner", "ok"),
        outcome, null, "abc", true, null);

    private static List<string> Chain(int count)
    {
        var lines = new List<string>();
        var previous = AuditChain.Genesis;
        for (var i = 0; i < count; i++)
        {
            var sealedRecord = AuditChain.Seal(Record($"op{i}"), previous);
            lines.Add(AuditChain.ToLine(sealedRecord));
            previous = sealedRecord.Hash;
        }
        return lines;
    }

    [Fact]
    public void An_untouched_chain_verifies_and_round_trips_through_text()
    {
        var result = AuditChain.Verify(Chain(25));

        result.Ok.Should().BeTrue(result.Problem);
        result.Records.Should().Be(25);
    }

    [Fact]
    public void An_empty_log_is_valid() => AuditChain.Verify([]).Ok.Should().BeTrue();

    [Fact]
    public void Editing_any_field_of_a_record_is_detected_at_that_record()
    {
        var lines = Chain(5);
        lines[2] = lines[2].Replace("\"Operator\":\"PC\\\\alice\"", "\"Operator\":\"PC\\\\mallory\"");

        var result = AuditChain.Verify(lines);

        result.Ok.Should().BeFalse();
        result.BrokenAtLine.Should().Be(3);
        result.Problem.Should().Contain("modified");
    }

    [Fact]
    public void Changing_an_outcome_is_detected()
    {
        var lines = Chain(3);
        var forged = AuditChain.Parse(lines[1])! with { Outcome = AuditOutcome.Denied };
        lines[1] = AuditChain.ToLine(forged);

        AuditChain.Verify(lines).Ok.Should().BeFalse();
    }

    [Fact]
    public void Deleting_inserting_or_reordering_records_is_detected()
    {
        var original = Chain(6);

        var deleted = original.Where((_, i) => i != 3).ToList();
        var swapped = original.ToList();
        (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
        var inserted = original.ToList();
        inserted.Insert(2, original[1]);

        AuditChain.Verify(deleted).BrokenAtLine.Should().Be(4);
        AuditChain.Verify(swapped).Ok.Should().BeFalse();
        AuditChain.Verify(inserted).Ok.Should().BeFalse();
    }

    [Fact]
    public void A_record_forged_with_a_recomputed_own_hash_still_breaks_the_chain_after_it()
    {
        var lines = Chain(4);
        var forged = AuditChain.Seal(AuditChain.Parse(lines[1])! with { Outcome = AuditOutcome.Failed }, AuditChain.Parse(lines[1])!.PrevHash);
        lines[1] = AuditChain.ToLine(forged);

        var result = AuditChain.Verify(lines);

        result.Ok.Should().BeFalse("the next record still points at the original hash");
        result.BrokenAtLine.Should().Be(3);
    }

    [Fact]
    public void Garbage_lines_are_reported_not_skipped()
    {
        var lines = Chain(2);
        lines.Insert(1, "this is not json");

        AuditChain.Verify(lines).Should().Match<AuditVerification>(v => !v.Ok && v.BrokenAtLine == 2);
    }

    [Fact]
    public void Truncating_the_tail_cannot_be_detected_from_the_file_alone_and_is_documented_as_such()
    {
        // This is a known, documented limit: dropping the newest records leaves a valid shorter chain.
        AuditChain.Verify(Chain(6).Take(4)).Ok.Should().BeTrue();
    }

    [Fact]
    public void Free_text_is_stripped_of_control_characters_and_limited_in_length()
    {
        AuditRecord.Clean("line1\nline2\r\n\t{\"forged\":true}").Should().NotContainAny("\n", "\r", "\t");
        AuditRecord.Clean(new string('x', 5000))!.Length.Should().BeLessThanOrEqualTo(AuditRecord.MaxText + 1);
        AuditRecord.Clean(null).Should().BeNull();
    }

    [Fact]
    public void An_injected_newline_cannot_create_a_second_record()
    {
        var record = Record() with { ErrorDetails = AuditRecord.Clean("oops\n{\"Operation\":\"forged\"}") };

        AuditChain.ToLine(AuditChain.Seal(record, AuditChain.Genesis)).Should().NotContain("\n");
    }
}

public class RestrictedFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-rf-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void CreateNew_refuses_to_overwrite()
    {
        var path = Path.Combine(_dir, "a.bin");
        File.WriteAllText(path, "precious");

        var act = () => RestrictedFile.CreateNew(path);

        act.Should().Throw<IOException>();
        File.ReadAllText(path).Should().Be("precious");
    }

    [Fact]
    public void Created_files_are_not_shared_while_open()
    {
        using var stream = RestrictedFile.CreateNew(Path.Combine(_dir, "b.bin"));

        var act = () => new FileStream(Path.Combine(_dir, "b.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void On_unix_files_and_directories_are_private_to_the_owner()
    {
        if (OperatingSystem.IsWindows()) return;

        using (RestrictedFile.CreateNew(Path.Combine(_dir, "c.bin"))) { }
        RestrictedFile.CreateDirectory(Path.Combine(_dir, "sub"));

        File.GetUnixFileMode(Path.Combine(_dir, "c.bin")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Path.Combine(_dir, "sub")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void CreateDirectory_creates_missing_parents_and_is_idempotent()
    {
        var nested = Path.Combine(_dir, "x", "y", "z");

        RestrictedFile.CreateDirectory(nested);
        RestrictedFile.CreateDirectory(nested);

        Directory.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void SecureDelete_removes_the_file_and_tolerates_missing_ones()
    {
        var path = Path.Combine(_dir, "d.bin");
        File.WriteAllBytes(path, new byte[100_000]);

        RestrictedFile.SecureDelete(path);
        RestrictedFile.SecureDelete(path);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void SecureDelete_overwrites_the_content_before_removal()
    {
        var path = Path.Combine(_dir, "e.bin");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 200_000).ToArray());
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        RestrictedFile.SecureDelete(path);

        // While another handle keeps the (now unlinked) data reachable, it must already be zeroed.
        var content = new byte[holder.Length];
        holder.Position = 0;
        holder.ReadExactly(content);
        content.Should().OnlyContain(b => b == 0);
    }
}
