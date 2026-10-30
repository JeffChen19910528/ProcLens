using FluentAssertions;
using ProcLens.Core.Audit;
using ProcLens.Infrastructure.Audit;

namespace ProcLens.Integration.Tests;

public class FileAuditLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-audit-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string LogPath => Path.Combine(_dir, "nested", "audit.jsonl");

    private static AuditRecord Record(string operation = "dump.create", AuditOutcome outcome = AuditOutcome.Succeeded) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "PC\\alice", false, "PC", operation, 42, "demo", null, null, "Mini", "out",
        new AuditAuthorization(true, "owner", "ok"), outcome, null, null, false, null);

    [Fact]
    public async Task Appending_creates_the_directory_and_builds_a_valid_chain_from_the_genesis_value()
    {
        var log = new FileAuditLog(LogPath);

        await log.AppendAsync(Record("a"));
        await log.AppendAsync(Record("b"));

        var records = log.ReadAll();
        records.Select(r => r.Operation).Should().Equal("a", "b");
        records[0].PrevHash.Should().Be(AuditChain.Genesis);
        records[1].PrevHash.Should().Be(records[0].Hash);
        log.Verify().Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_new_instance_continues_the_existing_chain()
    {
        await new FileAuditLog(LogPath).AppendAsync(Record("a"));
        await new FileAuditLog(LogPath).AppendAsync(Record("b"));

        new FileAuditLog(LogPath).Verify().Should().Match<AuditVerification>(v => v.Ok && v.Records == 2);
    }

    [Fact]
    public async Task Concurrent_writers_never_fork_or_corrupt_the_chain()
    {
        // Several independent instances stand in for several ProcLens processes appending at once.
        var writers = Enumerable.Range(0, 6).Select(_ => new FileAuditLog(LogPath)).ToArray();

        await Task.WhenAll(writers.SelectMany((log, w) =>
            Enumerable.Range(0, 15).Select(i => Task.Run(() => log.AppendAsync(Record($"w{w}-{i}"))))));

        var verification = new FileAuditLog(LogPath).Verify();
        verification.Ok.Should().BeTrue(verification.Problem);
        verification.Records.Should().Be(90);
        File.ReadAllLines(LogPath).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_log_can_be_read_and_verified_while_another_writer_holds_it()
    {
        var log = new FileAuditLog(LogPath);
        await log.AppendAsync(Record("a"));

        var reads = Enumerable.Range(0, 20).Select(_ => Task.Run(() => log.Verify().Ok));
        var writes = Enumerable.Range(0, 10).Select(i => Task.Run(() => log.AppendAsync(Record($"b{i}"))));

        (await Task.WhenAll(reads)).Should().OnlyContain(ok => ok);
        await Task.WhenAll(writes);
        log.Verify().Records.Should().Be(11);
    }

    [Fact]
    public async Task Appending_after_an_edited_tail_keeps_the_break_visible_instead_of_hiding_it()
    {
        var log = new FileAuditLog(LogPath);
        await log.AppendAsync(Record("a"));
        await File.AppendAllTextAsync(LogPath, "this line is garbage\n");

        await log.AppendAsync(Record("b"));

        var verification = log.Verify();
        verification.Ok.Should().BeFalse();
        verification.BrokenAtLine.Should().Be(2);
    }

    [Fact]
    public async Task An_unwritable_location_is_reported_so_privileged_operations_can_refuse_to_run()
    {
        var blocker = Path.Combine(_dir, "blocker");
        await File.WriteAllTextAsync(blocker, "a file where a directory is needed");
        var log = new FileAuditLog(Path.Combine(blocker, "audit.jsonl"));

        var ensure = () => log.EnsureAvailableAsync();
        var append = () => log.AppendAsync(Record());

        foreach (var attempt in new[] { ensure, (Func<Task>)(() => append()) })
        {
            var thrown = await Record_Exception(attempt);
            thrown.Should().NotBeNull();
            (thrown is IOException || thrown is UnauthorizedAccessException).Should().BeTrue($"was {thrown?.GetType().Name}");
        }
    }

    private static async Task<Exception?> Record_Exception(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task Checking_availability_does_not_add_a_record()
    {
        var log = new FileAuditLog(LogPath);

        await log.EnsureAvailableAsync();

        log.ReadAll().Should().BeEmpty();
        log.Verify().Ok.Should().BeTrue();
    }

    [Fact]
    public void A_missing_log_reads_as_empty_and_valid()
    {
        var log = new FileAuditLog(Path.Combine(_dir, "never-created.jsonl"));

        log.ReadAll().Should().BeEmpty();
        log.Verify().Should().Match<AuditVerification>(v => v.Ok && v.Records == 0);
    }
}
