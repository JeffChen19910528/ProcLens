using FluentAssertions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Core.Tests;

public class PolicyFileTrustEvaluationTests
{
    private const string System = "S-1-5-18";
    private const string Admins = "S-1-5-32-544";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string Users = "S-1-5-32-545";
    private const string Everyone = "S-1-1-0";
    private const string Alice = "S-1-5-21-1-2-3-1001";
    private const string CreatorOwner = "S-1-3-0";

    private const int Read = 0x1 | 0x20 | 0x80 | 0x20000 | 0x100000;               // read, execute, read attributes/permissions, synchronize
    private const int Full = 0x1F01FF;
    private const int Append = PolicyFileTrust.AppendData;

    private static (string, string) Owner(string sid) => (sid, sid);
    private static AclRule Allow(string sid, int rights, bool inheritOnly = false) => new(sid, sid, true, rights, inheritOnly);

    private static IReadOnlyList<AclRule> Protected() =>
        [Allow(System, Full), Allow(Admins, Full), Allow(Users, Read), Allow(CreatorOwner, Full, inheritOnly: true)];

    private static string? Evaluate(
        string fileOwner = Admins, IReadOnlyList<AclRule>? file = null, string dirOwner = Admins, IReadOnlyList<AclRule>? dir = null) =>
        PolicyFileTrust.EvaluateWindows(Owner(fileOwner), file ?? Protected(), Owner(dirOwner), dir ?? Protected());

    [Theory]
    [InlineData(System)]
    [InlineData(Admins)]
    [InlineData(TrustedInstaller)]
    public void A_file_and_folder_owned_by_the_system_and_readable_by_users_is_trusted(string owner) =>
        Evaluate(fileOwner: owner, dirOwner: owner).Should().BeNull();

    [Fact]
    public void A_file_owned_by_an_ordinary_user_is_not_trusted_even_if_nobody_else_can_write_it() =>
        Evaluate(fileOwner: Alice).Should().Contain("owned by").And.Contain(Alice);

    [Fact]
    public void A_folder_owned_by_an_ordinary_user_is_not_trusted_because_its_owner_can_replace_the_file() =>
        Evaluate(dirOwner: Alice).Should().Contain("folder is owned by");

    [Theory]
    [InlineData(Users)]
    [InlineData(Everyone)]
    [InlineData(Alice)]
    public void Write_access_to_the_file_for_anyone_else_makes_it_untrusted(string principal)
    {
        var file = Protected().Append(Allow(principal, PolicyFileTrust.WriteData)).ToList();

        Evaluate(file: file).Should().Contain(principal).And.Contain("can modify it");
    }

    [Theory]
    [InlineData(PolicyFileTrust.WriteData)]
    [InlineData(PolicyFileTrust.AppendData)]
    [InlineData(PolicyFileTrust.Delete)]
    [InlineData(PolicyFileTrust.ChangePermissions)]
    [InlineData(PolicyFileTrust.TakeOwnership)]
    [InlineData(Full)]
    public void Every_right_that_can_alter_or_take_over_the_file_counts(int right)
    {
        var file = Protected().Append(Allow(Users, right)).ToList();

        Evaluate(file: file).Should().NotBeNull();
    }

    [Fact]
    public void Creating_files_in_the_folder_counts_because_it_allows_replacing_the_policy()
    {
        var dir = Protected().Append(Allow(Users, PolicyFileTrust.WriteData)).ToList();

        Evaluate(dir: dir).Should().Contain("can add, replace or delete files in its folder");
    }

    [Theory]
    [InlineData(PolicyFileTrust.Delete)]
    [InlineData(PolicyFileTrust.DeleteSubdirectoriesAndFiles)]
    [InlineData(PolicyFileTrust.ChangePermissions)]
    [InlineData(PolicyFileTrust.TakeOwnership)]
    public void Deleting_or_taking_over_the_folder_counts(int right) =>
        Evaluate(dir: Protected().Append(Allow(Users, right)).ToList()).Should().NotBeNull();

    [Fact]
    public void Creating_subfolders_in_the_folder_does_not_matter()
    {
        // The default ProgramData ACL gives Users "append data" (create subfolders); that cannot replace our file.
        var dir = Protected().Append(Allow(Users, Append)).ToList();

        Evaluate(dir: dir).Should().BeNull();
    }

    [Fact]
    public void Inherit_only_entries_do_not_apply_to_the_object_itself()
    {
        var dir = Protected().Append(Allow(Users, Full, inheritOnly: true)).ToList();

        Evaluate(dir: dir).Should().BeNull("an inherit-only entry only affects children");
    }

    [Fact]
    public void Deny_entries_are_ignored_but_never_make_an_untrusted_grant_trusted()
    {
        var file = Protected().Append(new AclRule(Users, Users, Allow: false, PolicyFileTrust.WriteData, InheritOnly: false)).ToList();
        Evaluate(file: file).Should().BeNull("a deny entry grants nothing");

        var both = Protected().Append(Allow(Users, PolicyFileTrust.WriteData))
            .Append(new AclRule(Users, Users, false, PolicyFileTrust.WriteData, false)).ToList();
        Evaluate(file: both).Should().NotBeNull("the grant is still reported; the check is deliberately conservative");
    }

    [Fact]
    public void Trusted_principals_may_hold_any_right()
    {
        Evaluate(file: [Allow(System, Full), Allow(Admins, Full), Allow(TrustedInstaller, Full)]).Should().BeNull();
    }
}

public class MachinePolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-mp-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Policy(string json)
    {
        var path = Path.Combine(_dir, "policy.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string Permissive = """{ "AdminDumpsEnabled": true, "RequireReason": false, "Retention": { "MaxAgeDays": 0 } }""";

    [Fact]
    public void An_explicitly_chosen_policy_path_is_trusted_because_the_operator_chose_it()
    {
        var policy = MachinePolicy.Load(Policy(Permissive), overridden: true);

        policy.IgnoredBecause.Should().BeNull();
        policy.Admin.AdminDumpsEnabled.Should().BeTrue();
        policy.Retention.MaxAgeDays.Should().Be(0);
    }

    [Fact]
    public void A_missing_policy_is_not_an_error_and_means_defaults()
    {
        var policy = MachinePolicy.Load(Path.Combine(_dir, "none.json"), overridden: false);

        policy.Exists.Should().BeFalse();
        policy.IgnoredBecause.Should().BeNull();
        policy.Admin.AdminDumpsEnabled.Should().BeFalse();
        policy.Retention.MaxAgeDays.Should().Be(RetentionPolicy.DefaultMaxAgeDays);
    }

    [Fact]
    public void A_policy_at_the_default_location_that_an_ordinary_user_could_have_written_is_ignored()
    {
        // A file in a user's own temp directory is exactly that: owned by the user (Windows) or in a group/other-writable folder.
        var path = Policy(Permissive);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);

        var policy = MachinePolicy.Load(path, overridden: false);

        policy.IgnoredBecause.Should().NotBeNull();
        policy.Admin.AdminDumpsEnabled.Should().BeFalse("the planted file must not enable administrative dumps");
        policy.Admin.RequireReason.Should().BeTrue("nor weaken the justification rule");
        policy.Retention.MaxAgeDays.Should().Be(RetentionPolicy.DefaultMaxAgeDays, "nor shorten retention");
        policy.Admin.Source.Should().Contain("ignored").And.Contain("SECURITY.md");
        policy.Retention.Source.Should().Contain("ignored");
    }

    [Fact]
    public void On_unix_only_a_file_and_folder_nobody_else_can_write_are_trusted()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Policy(Permissive);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        PolicyFileTrust.WhyUntrusted(path).Should().BeNull();

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        PolicyFileTrust.WhyUntrusted(path).Should().Contain("writable by group or others");

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        PolicyFileTrust.WhyUntrusted(path).Should().Contain("folder is writable");
    }

    [Fact]
    public void A_symbolic_link_is_never_trusted()
    {
        var real = Policy(Permissive);
        var link = Path.Combine(_dir, "link.json");
        try { File.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        PolicyFileTrust.WhyUntrusted(link).Should().Contain("symbolic link");
    }

    [Fact]
    public void An_absent_file_has_nothing_to_distrust() =>
        PolicyFileTrust.WhyUntrusted(Path.Combine(_dir, "nope.json")).Should().BeNull();
}

public class AuditAnchorTests
{
    private static AuditRecord Record(string op) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "PC\\alice", false, "PC", op, null, null, null, null, null, null,
        new AuditAuthorization(true, "owner", "ok"), AuditOutcome.Succeeded, null, null, null, null);

    private static (List<string> Lines, List<string> Hashes) Chain(int n)
    {
        var lines = new List<string>();
        var hashes = new List<string>();
        var previous = AuditChain.Genesis;
        for (var i = 0; i < n; i++)
        {
            var sealedRecord = AuditChain.Seal(Record($"op{i}"), previous);
            lines.Add(AuditChain.ToLine(sealedRecord));
            hashes.Add(sealedRecord.Hash);
            previous = sealedRecord.Hash;
        }
        return (lines, hashes);
    }

    [Fact]
    public void Verification_reports_the_head_hash_to_store_elsewhere()
    {
        var (lines, hashes) = Chain(4);

        var result = AuditChain.Verify(lines);

        result.Ok.Should().BeTrue();
        result.HeadHash.Should().Be(hashes[^1]);
        AuditChain.Verify([]).HeadHash.Should().BeNull("an empty log has no head");
    }

    [Fact]
    public void An_anchor_that_is_in_the_chain_passes_even_after_more_records_were_appended()
    {
        var (lines, hashes) = Chain(6);

        AuditChain.Verify(lines, anchor: hashes[2]).Ok.Should().BeTrue();
        AuditChain.Verify(lines, anchor: hashes[5].ToUpperInvariant()).Ok.Should().BeTrue("hash comparison ignores case");
    }

    [Fact]
    public void Cutting_off_the_newest_records_is_detected_by_an_anchor_taken_earlier()
    {
        var (lines, hashes) = Chain(6);

        var result = AuditChain.Verify(lines.Take(3), anchor: hashes[4]);

        result.Ok.Should().BeFalse();
        result.BrokenAtLine.Should().BeNull("every remaining record is individually fine; the anchor is what is missing");
        result.Problem.Should().Contain("anchor");
    }

    [Fact]
    public void Rebuilding_the_whole_chain_consistently_defeats_the_hashes_but_not_the_anchor()
    {
        var (original, hashes) = Chain(5);

        // The attacker rewrites record 1 and recomputes every hash after it: a perfectly valid chain.
        var forgedLines = new List<string>();
        var previous = AuditChain.Genesis;
        for (var i = 0; i < 5; i++)
        {
            var record = AuditChain.Parse(original[i])! with { Outcome = i == 1 ? AuditOutcome.Failed : AuditOutcome.Succeeded };
            var sealedRecord = AuditChain.Seal(record, previous);
            forgedLines.Add(AuditChain.ToLine(sealedRecord));
            previous = sealedRecord.Hash;
        }

        AuditChain.Verify(forgedLines).Ok.Should().BeTrue("unkeyed hashes cannot tell: this is the documented limit");
        AuditChain.Verify(forgedLines, anchor: hashes[4]).Ok.Should().BeFalse("but an anchor saved beforehand exposes the rewrite");
    }
}

public class HostileKeyDerivationTests
{
    [Fact]
    public void Slots_asking_for_more_iterations_than_the_ceiling_are_not_executed()
    {
        using var password = SecretText.From("a long enough password");
        var source = new PasswordKekSource(password, PasswordKekSource.MinIterations);
        var descriptor = source.Create().Descriptor;
        BitConverter.GetBytes(PasswordKekSource.MaxIterations + 1).CopyTo(descriptor, 0);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = source.Resolve(descriptor);
        watch.Stop();

        result.Should().BeNull();
        watch.ElapsedMilliseconds.Should().BeLessThan(200, "it must be refused before any derivation work is done");
    }

    [Fact]
    public void Files_written_with_the_default_iteration_count_remain_readable()
    {
        using var password = SecretText.From("a long enough password");
        var material = new PasswordKekSource(password).Create();

        new PasswordKekSource(password).Resolve(material.Descriptor).Should().Equal(material.Kek);
    }
}
