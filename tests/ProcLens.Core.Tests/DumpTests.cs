using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;

namespace ProcLens.Core.Tests;

public class MinidumpReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] BuildDump(ulong flags = 0x1000, ushort arch = 9, uint threads = 25) => TestMinidump.Build(flags, arch, threads);

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dmp");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Reads_a_valid_dump()
    {
        var info = MinidumpReader.Read(Write(BuildDump()));

        info.IsValid.Should().BeTrue(info.Problem);
        info.Architecture.Should().Be("x64");
        info.OsMajor.Should().Be(10);
        info.OsBuild.Should().Be(26300);
        info.ThreadCount.Should().Be(25);
        info.ModuleCount.Should().BeNull("there is no module list stream");
        info.StreamTypes.Should().Equal(7u, 3u);
        info.TimeStamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    }

    [Theory]
    [InlineData(0x1000ul, DumpType.Mini)]
    [InlineData(0x1200ul, DumpType.Heap)]   // WithPrivateReadWriteMemory
    [InlineData(0x1802ul, DumpType.Full)]   // WithFullMemory
    public void Infers_the_dump_type_from_the_flags(ulong flags, DumpType expected) =>
        MinidumpReader.Read(Write(BuildDump(flags))).InferredType.Should().Be(expected);

    [Fact]
    public void Rejects_a_wrong_signature()
    {
        var bytes = BuildDump();
        bytes[0] = (byte)'X';

        var info = MinidumpReader.Read(Write(bytes));

        info.IsValid.Should().BeFalse();
        info.Problem.Should().Contain("MDMP");
    }

    [Fact]
    public void Rejects_a_truncated_file_whose_streams_run_past_the_end()
    {
        var bytes = BuildDump();

        var info = MinidumpReader.Read(Write(bytes[..^2]));

        info.IsValid.Should().BeFalse();
        info.Problem.Should().Contain("truncated");
    }

    [Fact]
    public void Rejects_tiny_and_empty_files()
    {
        MinidumpReader.Read(Write([])).IsValid.Should().BeFalse();
        MinidumpReader.Read(Write(new byte[10])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_implausible_stream_count_without_allocating_for_it()
    {
        var bytes = BuildDump();
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(bytes, 8);

        var info = MinidumpReader.Read(Write(bytes));

        info.IsValid.Should().BeFalse();
        info.Problem.Should().Contain("stream count");
    }

    [Fact]
    public void Rejects_a_directory_pointing_outside_the_file()
    {
        var bytes = BuildDump();
        BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(bytes, 12);

        MinidumpReader.Read(Write(bytes)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Reports_an_unknown_architecture_instead_of_guessing()
    {
        MinidumpReader.Read(Write(BuildDump(arch: 99))).Architecture.Should().Contain("unknown");
    }

    [Fact]
    public async Task Metadata_round_trips_through_its_sidecar_and_hash_matches_the_file()
    {
        var dump = Write(BuildDump());
        var metadata = new DumpMetadata(Guid.NewGuid(), 42, "demo", DateTimeOffset.UtcNow, DumpType.Heap, dump, 123,
            DateTimeOffset.UtcNow, "TestOS", "1.2.3", CollectionStatus.Succeeded, null, await DumpFiles.ComputeSha256Async(dump), "plainhash", null);
        File.WriteAllText(DumpFiles.SidecarPath(dump), DumpFiles.SerializeMetadata(metadata));

        var read = DumpFiles.TryReadMetadata(dump);

        read.Should().Be(metadata);
        (await DumpFiles.ComputeSha256Async(dump)).Should().Be(read!.Sha256).And.HaveLength(64);
    }

    [Fact]
    public void Missing_or_corrupt_metadata_reads_as_null()
    {
        var dump = Write(BuildDump());
        DumpFiles.TryReadMetadata(dump).Should().BeNull();

        File.WriteAllText(DumpFiles.SidecarPath(dump), "{ not json");
        DumpFiles.TryReadMetadata(dump).Should().BeNull();
    }
}

public class SameUserDumpAuthorizerTests
{
    private static AuthorizationDecision Decide(string? owner, string op = @"PC\alice") =>
        new SameUserDumpAuthorizer().Authorize(new DumpAuthorizationContext(1, owner, op));

    [Fact]
    public void Allows_the_operators_own_process_ignoring_case() =>
        Decide(@"pc\ALICE").Allowed.Should().BeTrue();

    [Fact]
    public void Denies_someone_elses_process_and_names_both_parties()
    {
        var decision = Decide(@"PC\bob");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("PC\\bob").And.Contain("PC\\alice");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Denies_when_the_owner_is_unknown_failing_closed(string? owner) =>
        Decide(owner).Allowed.Should().BeFalse();
}
