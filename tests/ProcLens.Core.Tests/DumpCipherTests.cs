using System.Security.Cryptography;
using FluentAssertions;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Core.Tests;

public class DumpCipherTests
{
    private const int Chunk = 4096;
    private const int Cheap = PasswordKekSource.MinIterations;

    /// <summary>Stands in for the machine/user key (which is DPAPI on Windows).</summary>
    private sealed class FixedKeySource(byte[] key) : IKekSource
    {
        public byte SlotType => 1;
        public KekMaterial Create() => new([0xAA], (byte[])key.Clone());
        public byte[]? Resolve(ReadOnlySpan<byte> descriptor) => descriptor.SequenceEqual(new byte[] { 0xAA }) ? (byte[])key.Clone() : null;
    }

    private static PasswordKekSource Password(string text) => new(SecretText.From(text), Cheap);
    private static FixedKeySource Machine(byte seed = 1) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static byte[] Plain(int length) => RandomNumberGenerator.GetBytes(length);

    private static async Task<byte[]> Encrypt(byte[] plain, params IKekSource[] sources)
    {
        using var output = new MemoryStream();
        await DumpCipher.EncryptAsync(new MemoryStream(plain), output, sources, Chunk);
        return output.ToArray();
    }

    private static async Task<byte[]> Decrypt(byte[] cipher, params IKekSource[] sources)
    {
        using var output = new MemoryStream();
        await DumpCipher.DecryptAsync(new MemoryStream(cipher), output, sources);
        return output.ToArray();
    }

    /// <summary>Offset of the first chunk: 32 fixed + 4 length + slots block.</summary>
    private static int ChunkStart(byte[] cipher) => 36 + BitConverter.ToInt32(cipher, 32);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData(3 * Chunk + 17)]
    public async Task Round_trips_at_every_chunk_boundary(int length)
    {
        var plain = Plain(length);

        var cipher = await Encrypt(plain, Machine());

        (await Decrypt(cipher, Machine())).Should().Equal(plain);
    }

    [Fact]
    public async Task Ciphertext_does_not_contain_the_plaintext_and_differs_every_time()
    {
        var plain = Enumerable.Repeat((byte)'S', 2 * Chunk).ToArray(); // very recognisable plaintext

        var a = await Encrypt(plain, Machine());
        var b = await Encrypt(plain, Machine());

        a.Should().NotEqual(b, "the data key, file id and nonce prefix are random per file");
        a.AsSpan().IndexOf(plain.AsSpan(0, 64)).Should().Be(-1);
    }

    [Fact]
    public async Task Header_can_be_read_without_any_key()
    {
        var cipher = await Encrypt(Plain(10), Machine(), Password("a long enough password"));

        var info = await DumpCipher.ReadInfoAsync(new MemoryStream(cipher));

        info.ChunkSize.Should().Be(Chunk);
        info.SlotTypes.Should().Equal((byte)1, PasswordKekSource.Type);
    }

    [Fact]
    public async Task Either_slot_unlocks_the_file()
    {
        var plain = Plain(5000);
        var cipher = await Encrypt(plain, Machine(), Password("correct horse battery"));

        (await Decrypt(cipher, Machine())).Should().Equal(plain);
        (await Decrypt(cipher, Password("correct horse battery"))).Should().Equal(plain);
    }

    [Fact]
    public async Task Wrong_password_or_wrong_machine_key_is_rejected_with_a_clear_error()
    {
        var cipher = await Encrypt(Plain(100), Password("correct horse battery"));

        var wrongPassword = () => Decrypt(cipher, Password("incorrect horse battery"));
        var wrongKind = () => Decrypt(cipher, Machine());

        (await wrongPassword.Should().ThrowAsync<DumpDecryptionException>()).Which.Message.Should().Contain("None of the available keys");
        await wrongKind.Should().ThrowAsync<DumpDecryptionException>();
    }

    [Fact]
    public async Task Machine_key_from_another_user_cannot_unlock()
    {
        var cipher = await Encrypt(Plain(100), Machine(1));

        var act = () => Decrypt(cipher, Machine(2));

        await act.Should().ThrowAsync<DumpDecryptionException>();
    }

    // ---------- tamper detection ----------

    [Fact]
    public async Task Any_flipped_bit_in_the_ciphertext_or_tag_is_detected()
    {
        var cipher = await Encrypt(Plain(3 * Chunk + 5), Machine());
        var start = ChunkStart(cipher);

        foreach (var offset in new[] { start, start + Chunk / 2, start + Chunk, start + Chunk + 16, cipher.Length - 1, cipher.Length - 20 })
        {
            var tampered = (byte[])cipher.Clone();
            tampered[offset] ^= 0x01;

            var act = () => Decrypt(tampered, Machine());

            await act.Should().ThrowAsync<DumpDecryptionException>($"flipping a bit at offset {offset} must be detected");
        }
    }

    [Fact]
    public async Task Truncation_at_a_chunk_boundary_is_detected_not_silently_accepted()
    {
        var cipher = await Encrypt(Plain(3 * Chunk), Machine()); // exactly 3 full chunks
        var oneChunkLess = cipher[..^(Chunk + DumpCipher.TagSize)];

        var act = () => Decrypt(oneChunkLess, Machine());

        await act.Should().ThrowAsync<DumpDecryptionException>("the new last chunk was not encrypted as final");
    }

    [Fact]
    public async Task Truncation_inside_a_chunk_and_extension_are_detected()
    {
        var cipher = await Encrypt(Plain(2 * Chunk + 10), Machine());

        await ((Func<Task>)(() => Decrypt(cipher[..^5], Machine()))).Should().ThrowAsync<DumpDecryptionException>();
        await ((Func<Task>)(() => Decrypt([.. cipher, 1, 2, 3], Machine()))).Should().ThrowAsync<DumpDecryptionException>();
        await ((Func<Task>)(() => Decrypt(cipher[..ChunkStart(cipher)], Machine()))).Should().ThrowAsync<DumpDecryptionException>("no chunks at all");
    }

    [Fact]
    public async Task Reordered_or_duplicated_chunks_are_detected()
    {
        var cipher = await Encrypt(Plain(3 * Chunk + 3), Machine());
        var start = ChunkStart(cipher);
        var size = Chunk + DumpCipher.TagSize;

        var swapped = (byte[])cipher.Clone();
        cipher.AsSpan(start + size, size).CopyTo(swapped.AsSpan(start));
        cipher.AsSpan(start, size).CopyTo(swapped.AsSpan(start + size));

        var duplicated = (byte[])cipher.Clone();
        cipher.AsSpan(start, size).CopyTo(duplicated.AsSpan(start + size));

        await ((Func<Task>)(() => Decrypt(swapped, Machine()))).Should().ThrowAsync<DumpDecryptionException>();
        await ((Func<Task>)(() => Decrypt(duplicated, Machine()))).Should().ThrowAsync<DumpDecryptionException>();
    }

    [Fact]
    public async Task Chunks_cannot_be_transplanted_between_files()
    {
        var a = await Encrypt(Plain(2 * Chunk), Machine());
        var b = await Encrypt(Plain(2 * Chunk), Machine());
        var spliced = (byte[])a.Clone();
        b.AsSpan(ChunkStart(b), Chunk + DumpCipher.TagSize).CopyTo(spliced.AsSpan(ChunkStart(a)));

        var act = () => Decrypt(spliced, Machine());

        await act.Should().ThrowAsync<DumpDecryptionException>();
    }

    [Fact]
    public async Task Modifying_the_fixed_header_invalidates_both_the_key_slot_and_the_chunks()
    {
        var cipher = await Encrypt(Plain(Chunk), Machine());
        var tampered = (byte[])cipher.Clone();
        tampered[20] ^= 0xFF; // inside the file id

        var act = () => Decrypt(tampered, Machine());

        await act.Should().ThrowAsync<DumpDecryptionException>();
    }

    [Fact]
    public async Task Failure_on_a_later_chunk_is_reported_even_though_earlier_output_was_produced()
    {
        var cipher = await Encrypt(Plain(3 * Chunk), Machine());
        cipher[ChunkStart(cipher) + 2 * (Chunk + DumpCipher.TagSize) + 5] ^= 1;
        using var output = new MemoryStream();

        var act = () => DumpCipher.DecryptAsync(new MemoryStream(cipher), output, [Machine()]);

        (await act.Should().ThrowAsync<DumpDecryptionException>()).Which.Message.Should().Contain("chunk 2");
        output.Length.Should().BeLessThan(3 * Chunk, "the caller is told to discard partial output; the bad chunk itself is never released");
    }

    // ---------- malformed input ----------

    [Fact]
    public async Task Garbage_and_tiny_inputs_fail_cleanly()
    {
        await ((Func<Task>)(() => Decrypt([], Machine()))).Should().ThrowAsync<DumpDecryptionException>();
        await ((Func<Task>)(() => Decrypt(new byte[10], Machine()))).Should().ThrowAsync<DumpDecryptionException>();
        await ((Func<Task>)(() => Decrypt(Plain(500), Machine()))).Should().ThrowAsync<DumpDecryptionException>("random data has no magic");
    }

    [Fact]
    public async Task Hostile_header_values_are_rejected_before_any_large_allocation()
    {
        var cipher = await Encrypt(Plain(10), Machine());

        var hugeSlots = (byte[])cipher.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(hugeSlots, 32);
        var hugeChunk = (byte[])cipher.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(hugeChunk, 8);
        var zeroSlots = (byte[])cipher.Clone();
        zeroSlots[36] = 0;

        foreach (var bad in new[] { hugeSlots, hugeChunk, zeroSlots })
            await ((Func<Task>)(() => Decrypt(bad, Machine()))).Should().ThrowAsync<DumpDecryptionException>();
    }

    [Fact]
    public async Task A_password_slot_asking_for_absurd_iteration_counts_is_ignored_not_executed()
    {
        var source = new PasswordKekSource(SecretText.From("a long enough password"), Cheap);
        var material = source.Create();
        var hostile = (byte[])material.Descriptor.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(hostile, 0);
        var tooCheap = (byte[])material.Descriptor.Clone();
        BitConverter.GetBytes(1).CopyTo(tooCheap, 0);

        source.Resolve(hostile).Should().BeNull();
        source.Resolve(tooCheap).Should().BeNull();
        source.Resolve(new byte[3]).Should().BeNull();
        source.Resolve(material.Descriptor).Should().NotBeNull();
        await Task.CompletedTask;
    }

    // ---------- re-wrapping (password-protected export) ----------

    [Fact]
    public async Task Rewrap_moves_the_file_to_a_password_without_re_encrypting_and_drops_the_old_key()
    {
        var plain = Plain(3 * Chunk + 9);
        var original = await Encrypt(plain, Machine());

        using var exported = new MemoryStream();
        await DumpCipher.RewrapAsync(new MemoryStream(original), exported, [Machine()], [Password("export password 123")]);
        var bytes = exported.ToArray();

        (await Decrypt(bytes, Password("export password 123"))).Should().Equal(plain);
        await ((Func<Task>)(() => Decrypt(bytes, Machine()))).Should().ThrowAsync<DumpDecryptionException>("the machine slot was not carried over");
        bytes.AsSpan(ChunkStart(bytes)).SequenceEqual(original.AsSpan(ChunkStart(original))).Should().BeTrue("chunks are copied verbatim");
    }

    [Fact]
    public async Task Rewrap_requires_the_ability_to_unlock_the_source_file()
    {
        var original = await Encrypt(Plain(100), Machine(1));

        var act = () => DumpCipher.RewrapAsync(new MemoryStream(original), new MemoryStream(), [Machine(2)], [Password("export password 123")]);

        await act.Should().ThrowAsync<DumpDecryptionException>();
    }

    // ---------- secrets ----------

    [Fact]
    public void Secret_text_never_reveals_itself_through_formatting_or_records()
    {
        using var secret = SecretText.From("hunter2-hunter2");
        var holder = new { Password = secret };

        secret.ToString().Should().Be("[redacted]");
        holder.ToString().Should().NotContain("hunter2");
        System.Text.Json.JsonSerializer.Serialize(secret).Should().NotContain("hunter2");
    }

    [Fact]
    public void Disposing_secret_text_wipes_it()
    {
        var secret = SecretText.From("hunter2-hunter2");
        secret.Dispose();

        secret.Length.Should().Be(0);
        secret.ToUtf8().Should().BeEmpty();
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactly12chr", true)]
    [InlineData("a considerably longer passphrase", true)]
    public void Password_strength_minimum_is_enforced(string password, bool accepted)
    {
        using var secret = SecretText.From(password);

        (PasswordKekSource.CheckStrength(secret) is null).Should().Be(accepted);
    }
}
