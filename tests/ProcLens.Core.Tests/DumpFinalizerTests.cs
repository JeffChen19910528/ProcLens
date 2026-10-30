using System.Security.Cryptography;
using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Core.Tests;

public class DumpFinalizerTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-fin-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FixedKey(byte seed = 1) : IKekSource
    {
        public byte SlotType => 1;
        public KekMaterial Create() => new([0xAA], Enumerable.Repeat(seed, 32).ToArray());
        public byte[]? Resolve(ReadOnlySpan<byte> d) => d.SequenceEqual(new byte[] { 0xAA }) ? Enumerable.Repeat(seed, 32).ToArray() : null;
    }

    private sealed class ThrowingKey(Exception exception) : IKekSource
    {
        public byte SlotType => 1;
        public KekMaterial Create() => throw exception;
        public byte[]? Resolve(ReadOnlySpan<byte> d) => null;
    }

    private sealed class Reports : IProgress<DumpProgress>
    {
        public List<long> Values { get; } = [];
        public void Report(DumpProgress value) => Values.Add(value.BytesWritten);
    }

    private (string Raw, string Final) Paths(bool encrypted)
    {
        var stem = "demo_7_mini_20260101T000000Z_deadbeef";
        return (DumpFileNames.RawPath(_dir, stem, encrypted), DumpFileNames.FinalPath(_dir, stem, encrypted));
    }

    private static FinalizeRequest Request(string raw, string final, IReadOnlyList<IKekSource> keys, Func<string, string?>? check = null, IProgress<DumpProgress>? progress = null) =>
        new(raw, final, keys, 7, "demo", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), DumpType.Mini, check, progress);

    private string[] Files() => Directory.GetFileSystemEntries(_dir);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---------- unprotected ----------

    [Fact]
    public async Task An_unprotected_dump_is_verified_hashed_and_recorded_in_place()
    {
        var (raw, final) = Paths(encrypted: false);
        var bytes = TestMinidump.BuildLarge(5000);
        await File.WriteAllBytesAsync(raw, bytes);
        var progress = new Reports();

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, [], progress: progress), default);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Metadata!.Should().Match<DumpMetadata>(m =>
            m.Sha256 == Sha(bytes) && m.PlaintextSha256 == Sha(bytes) && m.EncryptionScheme == null &&
            m.FileSize == bytes.Length && m.DumpFilePath == final && m.ProcessId == 7 && m.CollectionStatus == CollectionStatus.Succeeded);
        DumpFiles.TryReadMetadata(final).Should().Be(result.Metadata);
        File.ReadAllBytes(final).Should().Equal(bytes);
        progress.Values.Should().Equal(bytes.Length);
        Files().Should().HaveCount(2);
    }

    // ---------- protected ----------

    [Fact]
    public async Task A_protected_dump_ends_up_encrypted_with_both_hashes_right_and_no_raw_copy_left()
    {
        var (raw, final) = Paths(encrypted: true);
        var bytes = TestMinidump.BuildLarge(3 * DumpCipher.DefaultChunkSize + 123);   // several chunks
        await File.WriteAllBytesAsync(raw, bytes);
        var key = new FixedKey();

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, [key]), default);

        result.Succeeded.Should().BeTrue(result.Message);
        File.Exists(raw).Should().BeFalse("the raw copy is wiped once the encrypted copy has been proven readable");
        var stored = await File.ReadAllBytesAsync(final);
        result.Metadata!.Sha256.Should().Be(Sha(stored), "Sha256 describes the file as stored");
        result.Metadata.PlaintextSha256.Should().Be(Sha(bytes), "computed during the same pass that encrypts");
        result.Metadata.EncryptionScheme.Should().Be(DumpCipher.SchemeName);
        Files().Should().HaveCount(2).And.NotContain(f => f.EndsWith(".tmp"));

        using var restored = new MemoryStream();
        await DumpCipher.DecryptAsync(new MemoryStream(stored), restored, [key]);
        restored.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task Every_key_source_can_open_the_result()
    {
        var (raw, final) = Paths(encrypted: true);
        var bytes = TestMinidump.Build();
        await File.WriteAllBytesAsync(raw, bytes);
        using var password = SecretText.From("a long enough password");
        var password1 = new PasswordKekSource(password, PasswordKekSource.MinIterations);

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, [new FixedKey(), password1]), default);

        result.Succeeded.Should().BeTrue(result.Message);
        foreach (var source in new IKekSource[] { new FixedKey(), password1 })
        {
            using var restored = new MemoryStream();
            await DumpCipher.DecryptAsync(new MemoryStream(await File.ReadAllBytesAsync(final)), restored, [source]);
            restored.ToArray().Should().Equal(bytes);
        }
    }

    // ---------- failures leave nothing behind ----------

    [Fact]
    public async Task An_empty_dump_is_a_failure_and_is_removed()
    {
        var (raw, final) = Paths(encrypted: false);
        await File.WriteAllBytesAsync(raw, []);

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, []), default);

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("empty");
        Files().Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_not_a_minidump_is_a_failure_and_is_removed()
    {
        var (raw, final) = Paths(encrypted: true);
        await File.WriteAllBytesAsync(raw, RandomNumberGenerator.GetBytes(4000));

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, [new FixedKey()]), default);

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("verification");
        Files().Should().BeEmpty("neither the raw dump nor any ciphertext may be left");
    }

    [Fact]
    public async Task The_platform_check_can_veto_a_structurally_valid_dump()
    {
        var (raw, final) = Paths(encrypted: false);
        await File.WriteAllBytesAsync(raw, TestMinidump.Build());

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, [], check: _ => "DbgHelp could not read stream 7."), default);

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("DbgHelp could not read stream 7.");
        Files().Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_failure_is_reported_and_leaves_no_raw_dump_ciphertext_or_metadata()
    {
        var (raw, final) = Paths(encrypted: true);
        await File.WriteAllBytesAsync(raw, TestMinidump.Build());

        var result = await new DumpFinalizer().FinalizeAsync(
            Request(raw, final, [new ThrowingKey(new CryptographicException("master key unavailable"))]), default);

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("master key unavailable");
        Files().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_propagates_and_cleans_up()
    {
        var (raw, final) = Paths(encrypted: true);
        await File.WriteAllBytesAsync(raw, TestMinidump.Build());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new DumpFinalizer().FinalizeAsync(Request(raw, final, [new FixedKey()]), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Files().Should().BeEmpty();
    }

    // ---------- only what it created ----------

    [Fact]
    public async Task A_file_that_already_exists_under_the_final_name_is_never_touched_even_when_the_dump_fails()
    {
        var (raw, final) = Paths(encrypted: true);
        await File.WriteAllBytesAsync(raw, TestMinidump.Build());
        await File.WriteAllTextAsync(final, "somebody else's file");

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, [new FixedKey()]), default);

        result.Status.Should().Be(DumpStatus.Failed);
        (await File.ReadAllTextAsync(final)).Should().Be("somebody else's file");
        File.Exists(raw).Should().BeFalse("the raw dump is ours and is still wiped");
    }

    [Fact]
    public async Task A_metadata_file_that_already_exists_is_never_touched_either()
    {
        var (raw, final) = Paths(encrypted: false);
        await File.WriteAllBytesAsync(raw, TestMinidump.Build());
        await File.WriteAllTextAsync(DumpFiles.SidecarPath(final), "not ours");

        var result = await new DumpFinalizer().FinalizeAsync(Request(raw, final, []), default);

        result.Succeeded.Should().BeFalse();
        (await File.ReadAllTextAsync(DumpFiles.SidecarPath(final))).Should().Be("not ours");
        File.Exists(raw).Should().BeFalse("a dump that could not be recorded is not kept");
    }
}

public class DumpOutputDirectoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-odir-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_directory_argument_is_refused(string? requested)
    {
        var result = DumpOutputDirectory.Prepare(requested);

        result.IsUsable.Should().BeFalse();
        result.Problem.Should().Contain("required");
    }

    [Fact]
    public void A_missing_directory_is_created_and_an_existing_one_is_used()
    {
        var nested = Path.Combine(_dir, "a", "b");

        var created = DumpOutputDirectory.Prepare(nested);
        var existing = DumpOutputDirectory.Prepare(nested);

        created.Path.Should().Be(nested);
        Directory.Exists(nested).Should().BeTrue();
        existing.Path.Should().Be(nested);
    }

    [Fact]
    public void A_file_is_not_a_directory()
    {
        var file = Path.Combine(_dir, "afile");
        File.WriteAllText(file, "x");

        DumpOutputDirectory.Prepare(file).Problem.Should().Contain("is a file");
    }

    [Fact]
    public void Paths_that_would_not_make_a_normal_directory_are_refused_by_the_shared_rule()
    {
        DumpOutputDirectory.Prepare("bad\u0001dir").IsUsable.Should().BeFalse();
        if (OperatingSystem.IsWindows())
        {
            DumpOutputDirectory.Prepare(Path.Combine(_dir, "x:stream")).IsUsable.Should().BeFalse();
            DumpOutputDirectory.Prepare(Path.Combine(_dir, "NUL")).IsUsable.Should().BeFalse();
        }
    }

    [Fact]
    public void A_symbolic_link_is_refused_so_the_dump_cannot_be_redirected()
    {
        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        var link = Path.Combine(_dir, "link");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        DumpOutputDirectory.Prepare(link).Problem.Should().Contain("symbolic link");
    }
}

public class DumpFileNamesTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void The_stem_has_the_documented_shape()
    {
        var stem = DumpFileNames.Stem("PING", 32528, DumpType.Heap, At);

        stem.Should().MatchRegex(@"^PING_32528_heap_20260304T050607Z_[0-9a-f]{8}$");
    }

    [Theory]
    [InlineData(@"..\..\windows\evil")]
    [InlineData("../../etc/passwd")]
    [InlineData("a b:c*d?e<f>g|h")]
    [InlineData("na\0me\r\n")]
    public void A_hostile_process_name_can_never_escape_the_directory_or_inject_characters(string name)
    {
        var stem = DumpFileNames.Stem(name, 1, DumpType.Mini, At);

        stem.IndexOfAny(['/', '\\', ':', '*', '?', '<', '>', '|', '\0', '\r', '\n', ' ']).Should().Be(-1);
        Path.GetFileName(DumpFileNames.FinalPath("/out", stem, true)).Should().Be(stem + ".dmp.enc");
    }

    [Fact]
    public void An_unusable_name_falls_back_and_a_long_name_is_cut()
    {
        DumpFileNames.Stem("***", 1, DumpType.Mini, At).Should().StartWith("process_1_");
        DumpFileNames.Stem(new string('a', 500), 1, DumpType.Mini, At).Length.Should().BeLessThan(90);
    }

    [Fact]
    public void Two_stems_for_the_same_process_and_second_differ()
    {
        DumpFileNames.Stem("p", 1, DumpType.Mini, At).Should().NotBe(DumpFileNames.Stem("p", 1, DumpType.Mini, At));
    }

    [Theory]
    [InlineData(true, ".dmp.plain.tmp", ".dmp.enc")]
    [InlineData(false, ".dmp", ".dmp")]
    public void Raw_and_final_names_depend_on_whether_the_dump_will_be_encrypted(bool encrypted, string raw, string final)
    {
        DumpFileNames.RawPath("/o", "stem", encrypted).Should().EndWith("stem" + raw);
        DumpFileNames.FinalPath("/o", "stem", encrypted).Should().EndWith("stem" + final);
    }
}

public class HashingStreamTests
{
    [Fact]
    public async Task Reading_through_the_wrapper_hashes_what_was_read_and_leaves_the_inner_stream_open()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        using var inner = new MemoryStream(data);
        using var wrapper = new HashingReadStream(inner);

        using var copy = new MemoryStream();
        await wrapper.CopyToAsync(copy);

        copy.ToArray().Should().Equal(data);
        wrapper.Sha256Hex.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));
        inner.CanRead.Should().BeTrue();
    }

    [Fact]
    public async Task Writing_through_the_wrapper_hashes_and_passes_the_bytes_on()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        using var inner = new MemoryStream();
        using var wrapper = new HashingWriteStream(inner);

        await wrapper.WriteAsync(data.AsMemory(0, 100_000));
        wrapper.Write(data, 100_000, 200_000);

        inner.ToArray().Should().Equal(data);
        wrapper.Sha256Hex.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));
        inner.CanWrite.Should().BeTrue();
    }

    [Fact]
    public async Task Hashing_an_empty_stream_gives_the_empty_hash()
    {
        using var wrapper = new HashingReadStream(new MemoryStream());
        await wrapper.CopyToAsync(Stream.Null);

        wrapper.Sha256Hex.Should().Be(Convert.ToHexStringLower(SHA256.HashData([])));
    }
}
