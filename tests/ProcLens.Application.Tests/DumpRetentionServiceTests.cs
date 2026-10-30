using FluentAssertions;
using ProcLens.Application.Dumps;
using ProcLens.Application.Tests.Support;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Application.Tests;

public class DumpRetentionServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-retsvc-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly InMemoryAuditLog _audit = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private int _sequence;

    public void Dispose() => Directory.Delete(_dir, true);

    private DumpRetentionService Service() => new(_audit, new FakeOperator(), TimeProvider.System);

    /// <summary>Creates a dump file plus the metadata ProcLens would have written, with a chosen creation time.</summary>
    private (string Dump, string Sidecar) Managed(double daysOld, bool encrypted = true, byte[]? content = null, string? name = null)
    {
        var suffix = encrypted ? ".dmp.enc" : ".dmp";
        var path = Path.Combine(_dir, name ?? $"demo_{++_sequence}_mini_20260101T000000Z_{_sequence:x8}{suffix}");
        File.WriteAllBytes(path, content ?? (encrypted ? new byte[] { 0x50, 0x4C, 0x45, 0x4E, 0x43, 0x30, 0x30, 0x31, 1, 2, 3 } : new byte[4096]));
        var metadata = new DumpMetadata(Guid.NewGuid(), 42, "demo", _now.AddDays(-daysOld - 1), DumpType.Mini, path, new FileInfo(path).Length,
            _now.AddDays(-daysOld), "OS", "1", CollectionStatus.Succeeded, null, "stored", "plain", encrypted ? DumpCipher.SchemeName : null);
        var sidecar = DumpFiles.SidecarPath(path);
        File.WriteAllText(sidecar, DumpFiles.SerializeMetadata(metadata));
        return (path, sidecar);
    }

    private static readonly RetentionPolicy Thirty = new(MaxAgeDays: 30);

    // ---------- dry run ----------

    [Fact]
    public async Task A_dry_run_lists_what_would_go_and_deletes_and_audits_nothing()
    {
        var (old, oldSidecar) = Managed(60);
        var (recent, _) = Managed(1);

        var report = await Service().RunAsync(_dir, Thirty, delete: false);

        report.DryRun.Should().BeTrue();
        report.Items.Should().ContainSingle().Which.Should().Match<RetentionItem>(i => i.Path == old && i.Status == RetentionItemStatus.Planned);
        report.Kept.Should().Be(1);
        File.Exists(old).Should().BeTrue();
        File.Exists(oldSidecar).Should().BeTrue();
        File.Exists(recent).Should().BeTrue();
        _audit.Records.Should().BeEmpty("nothing changed");
        report.BytesFreed.Should().Be(0);
        report.BytesSelected.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_dry_run_works_even_when_the_audit_trail_is_unavailable()
    {
        Managed(60);
        _audit.Unavailable = true;

        var report = await Service().RunAsync(_dir, Thirty, delete: false);

        report.Problem.Should().BeNull();
        report.Items.Should().HaveCount(1);
    }

    // ---------- deletion ----------

    [Fact]
    public async Task Deleting_removes_the_dump_and_its_metadata_and_keeps_everything_else()
    {
        var (old, oldSidecar) = Managed(60);
        var (recent, recentSidecar) = Managed(2);

        var report = await Service().RunAsync(_dir, Thirty, delete: true);

        File.Exists(old).Should().BeFalse();
        File.Exists(oldSidecar).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.Exists(recentSidecar).Should().BeTrue();
        report.Items.Should().ContainSingle().Which.Status.Should().Be(RetentionItemStatus.Deleted);
        report.BytesFreed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Every_removal_is_audited_with_its_reason_and_target()
    {
        Managed(60);
        Managed(45);
        Managed(1);

        await Service().RunAsync(_dir, Thirty, delete: true);

        _audit.Records.Should().HaveCount(2).And.OnlyContain(r =>
            r.Operation == "dump.delete" && r.Outcome == AuditOutcome.Succeeded && r.TargetPid == 42 && r.TargetName == "demo" &&
            r.Authorization.Policy == "retention" && r.Authorization.Detail.Contains("older than 30") && r.Encrypted == true &&
            r.FileSha256 == "stored" && r.Operator == @"PC\alice");
        AuditChain.Verify(_audit.Records.Select(AuditChain.ToLine)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_is_deleted_when_the_audit_trail_cannot_be_written()
    {
        var (old, _) = Managed(60);
        _audit.Unavailable = true;

        var report = await Service().RunAsync(_dir, Thirty, delete: true);

        report.Problem.Should().Contain("audit log is not writable");
        File.Exists(old).Should().BeTrue("removals must be accountable");
    }

    [Fact]
    public async Task A_failed_audit_write_after_a_removal_is_surfaced_as_a_warning()
    {
        Managed(60);
        _audit.FailOnAppend = true;

        var report = await Service().RunAsync(_dir, Thirty, delete: true);

        report.Items.Single().Status.Should().Be(RetentionItemStatus.Deleted);
        report.Warnings.Should().ContainSingle(w => w.Contains("could not be written"));
    }

    [Fact]
    public async Task A_dump_that_cannot_be_removed_is_reported_failed_audited_and_keeps_its_metadata()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix lets you unlink a file that is open
        var (old, oldSidecar) = Managed(60);
        using var holder = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read); // blocks deletion

        var report = await Service().RunAsync(_dir, Thirty, delete: true);

        report.HasFailures.Should().BeTrue();
        report.Items.Single().Should().Match<RetentionItem>(i => i.Status == RetentionItemStatus.Failed && i.Error != null);
        File.Exists(old).Should().BeTrue();
        File.Exists(oldSidecar).Should().BeTrue("the metadata must survive so the dump stays managed");
        _audit.Records.Single().Outcome.Should().Be(AuditOutcome.Failed);
    }

    // ---------- what is (not) managed ----------

    [Fact]
    public async Task Only_dumps_with_valid_matching_metadata_are_ever_touched()
    {
        var unrelated = Path.Combine(_dir, "notes.txt");
        var bareDump = Path.Combine(_dir, "stray.dmp");
        var orphanSidecar = Path.Combine(_dir, "ghost.dmp.json");
        var garbage = Path.Combine(_dir, "bad.dmp");
        File.WriteAllText(unrelated, "my notes");
        File.WriteAllBytes(bareDump, new byte[100]);
        File.WriteAllText(orphanSidecar, "{}");
        File.WriteAllBytes(garbage, new byte[100]);
        File.WriteAllText(DumpFiles.SidecarPath(garbage), "{ definitely not metadata");
        var (liar, _) = Managed(60, name: "liar.dmp");
        File.WriteAllText(DumpFiles.SidecarPath(liar),
            DumpFiles.SerializeMetadata(DumpFiles.TryReadMetadata(liar)! with { DumpFilePath = Path.Combine(_dir, "somewhere-else.dmp") }));

        var report = await Service().RunAsync(_dir, new RetentionPolicy(MaxAgeDays: 0), delete: true);

        report.Items.Should().BeEmpty();
        new[] { unrelated, bareDump, orphanSidecar, garbage, liar }.Should().OnlyContain(f => File.Exists(f));
        report.Skipped.Should().Contain(s => s.StartsWith("ghost.dmp.json")).And.Contain(s => s.StartsWith("bad.dmp.json"))
            .And.Contain(s => s.StartsWith("liar.dmp.json") && s.Contains("different file"));
    }

    [Fact]
    public async Task Subdirectories_are_not_entered()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "nested")).FullName;
        var path = Path.Combine(sub, "inner.dmp.enc");
        File.WriteAllBytes(path, new byte[10]);
        File.WriteAllText(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(new DumpMetadata(
            Guid.NewGuid(), 1, "x", _now, DumpType.Mini, path, 10, _now.AddDays(-99), "OS", "1", CollectionStatus.Succeeded, null, "a", "b", null)));

        var report = await Service().RunAsync(_dir, new RetentionPolicy(MaxAgeDays: 0), delete: true);

        report.Items.Should().BeEmpty();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Symbolic_links_are_never_followed_or_removed()
    {
        var (target, _) = Managed(60, name: "real.dmp.enc");
        var link = Path.Combine(_dir, "linked.dmp.enc");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // not permitted on this machine
        File.Copy(DumpFiles.SidecarPath(target), DumpFiles.SidecarPath(link));

        var report = await Service().RunAsync(_dir, new RetentionPolicy(MaxAgeDays: 0), delete: true);

        report.Skipped.Should().Contain(s => s.Contains("symbolic links"));
        File.Exists(link).Should().BeTrue("the link itself must not be removed");
    }

    [Fact]
    public async Task A_missing_directory_or_a_directory_symlink_is_a_problem_not_a_crash()
    {
        (await Service().RunAsync(Path.Combine(_dir, "nope"), Thirty, delete: true)).Problem.Should().Contain("not found");

        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        var link = Path.Combine(_dir, "dirlink");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        (await Service().RunAsync(link, Thirty, delete: true)).Problem.Should().Contain("symbolic link");
    }

    // ---------- interrupted dumps ----------

    [Fact]
    public async Task Abandoned_temporary_plaintext_is_removed_but_a_fresh_one_is_left_alone()
    {
        var stale = Path.Combine(_dir, "demo_42_heap_20260101T000000Z_0a1b2c3d.dmp.plain.tmp");
        var fresh = Path.Combine(_dir, "demo_42_heap_20260101T000001Z_0a1b2c3e.dmp.plain.tmp");
        var lookalike = Path.Combine(_dir, "something.dmp.plain.tmp");
        foreach (var f in new[] { stale, fresh, lookalike }) File.WriteAllBytes(f, new byte[2000]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - DumpRetentionService.AbandonedAfter - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(lookalike, DateTime.UtcNow.AddDays(-30));

        var report = await Service().RunAsync(_dir, Thirty, delete: true);

        File.Exists(stale).Should().BeFalse("an interrupted dump left raw memory behind");
        File.Exists(fresh).Should().BeTrue("it may belong to a dump that is still being written");
        File.Exists(lookalike).Should().BeTrue("only ProcLens-generated names are ever removed");
        report.Skipped.Should().Contain(s => s.Contains("still in progress"));
        _audit.Records.Single().Authorization.Detail.Should().Contain("interrupted dump");
    }

    // ---------- secure deletion ----------

    [Fact]
    public async Task Unencrypted_dumps_are_overwritten_with_zeros_before_removal_but_encrypted_ones_are_simply_deleted()
    {
        var secret = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        var (plain, _) = Managed(60, encrypted: false, content: secret);
        var (cipher, _) = Managed(61, encrypted: true, content: [.. "PLENC001"u8.ToArray(), .. secret]);

        // A second handle keeps the unlinked data reachable so we can look at what was left in it.
        using var plainView = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var cipherView = new FileStream(cipher, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        await Service().RunAsync(_dir, Thirty, delete: true);

        var left = new byte[secret.Length];
        plainView.Position = 0;
        plainView.ReadExactly(left);
        left.Should().OnlyContain(b => b == 0, "raw memory is wiped before the file is unlinked");

        cipherView.Position = 8;
        cipherView.ReadExactly(left);
        left.Should().Equal(secret, "ciphertext needs no wiping; its protection is the key");
        File.Exists(plain).Should().BeFalse();
        File.Exists(cipher).Should().BeFalse();
    }

    // ---------- combined rules through the service ----------

    [Fact]
    public async Task Count_and_size_rules_apply_through_the_service()
    {
        var newest = Managed(1).Dump;
        var middle = Managed(2).Dump;
        var oldest = Managed(3).Dump;

        var report = await Service().RunAsync(_dir, new RetentionPolicy(365, MaxCount: 2), delete: true);

        File.Exists(newest).Should().BeTrue();
        File.Exists(middle).Should().BeTrue();
        File.Exists(oldest).Should().BeFalse();
        report.Items.Single().Reason.Should().Contain("newest 2");
    }
}
