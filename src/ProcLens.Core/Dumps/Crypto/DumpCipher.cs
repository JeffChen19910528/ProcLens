using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ProcLens.Core.Dumps.Crypto;

public enum DecryptionFailure
{
    /// <summary>Not a ProcLens encrypted dump, or its header is malformed.</summary>
    Format,

    /// <summary>No available key (password, machine key) unlocks any key slot.</summary>
    KeyUnavailable,

    /// <summary>The key was right but the data failed authentication: corrupt, truncated, extended or modified.</summary>
    Authentication,
}

public sealed class DumpDecryptionException(string message, DecryptionFailure kind, Exception? inner = null) : Exception(message, inner)
{
    public DecryptionFailure Kind { get; } = kind;
}

public sealed record EncryptedFileInfo(int ChunkSize, IReadOnlyList<byte> SlotTypes);

/// <summary>
/// Chunked, streaming AES-256-GCM for dump files (which can be many gigabytes, so the one-shot AES-GCM API is
/// unusable). A random data key per file is wrapped by one or more key-encryption keys ("slots").
///
/// Layout:  magic[8] "PLENC001" | chunkSize u32 | fileId[16] | noncePrefix[4]   (the 32 bytes before slotsLength
///          are the fixed header)  | slotsLength u32 | slots | chunk*
/// Slot:    type u8 | descriptorLength u8 | descriptor | nonce[12] | wrappedKey[32] | tag[16]
/// Chunk:   ciphertext (chunkSize bytes, fewer in the last chunk) | tag[16]
///
/// Every chunk is authenticated with associated data = fixedHeader | chunkIndex u64 | isFinal u8 and a nonce built
/// from the file's prefix and the index, so a chunk cannot be reordered, dropped, duplicated or moved to another
/// file, and truncating or extending the file is detected (the last chunk must carry the "final" flag).
/// Slots are not part of the chunk associated data, so a file can be re-wrapped for a new key without re-encrypting.
/// </summary>
public static class DumpCipher
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 1 << 20;
    public const string SchemeName = "PLENC001/AES-256-GCM-chunked";

    private const int FixedHeaderSize = 32;
    private const int MinChunkSize = 4096;
    private const int MaxChunkSize = 64 << 20;
    private const int MaxSlotsLength = 4096;
    private const int MaxSlots = 8;
    private static ReadOnlySpan<byte> Magic => "PLENC001"u8;

    private sealed record Slot(byte Type, byte[] Descriptor, byte[] Nonce, byte[] Wrapped, byte[] Tag);

    private sealed record Header(byte[] Fixed, int ChunkSize, List<Slot> Slots);

    public static bool IsEncrypted(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> buffer = stackalloc byte[8];
            return stream.ReadAtLeast(buffer, 8, throwOnEndOfStream: false) == 8 && buffer.SequenceEqual(Magic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<EncryptedFileInfo> ReadInfoAsync(Stream input, CancellationToken ct = default)
    {
        var header = await ReadHeaderAsync(input, ct);
        return new EncryptedFileInfo(header.ChunkSize, header.Slots.Select(s => s.Type).ToList());
    }

    public static async Task EncryptAsync(
        Stream input, Stream output, IReadOnlyList<IKekSource> sources, int chunkSize = DefaultChunkSize, CancellationToken ct = default)
    {
        if (sources.Count is 0 or > MaxSlots) throw new ArgumentException("One to eight key sources are required.", nameof(sources));
        if (chunkSize is < MinChunkSize or > MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var fixedHeader = new byte[FixedHeaderSize];
        Magic.CopyTo(fixedHeader);
        BinaryPrimitives.WriteInt32LittleEndian(fixedHeader.AsSpan(8), chunkSize);
        RandomNumberGenerator.Fill(fixedHeader.AsSpan(12, 20)); // fileId[16] + noncePrefix[4]

        var dek = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var slots = sources.Select(s => WrapSlot(s, dek, fixedHeader)).ToList();
            await WriteHeaderAsync(output, fixedHeader, slots, ct);
            await EncryptChunksAsync(input, output, dek, fixedHeader, chunkSize, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Decrypts and authenticates. Plaintext is written chunk by chunk as each chunk verifies, so on failure the caller must discard the output.</summary>
    public static async Task DecryptAsync(Stream input, Stream output, IReadOnlyList<IKekSource> sources, CancellationToken ct = default)
    {
        var header = await ReadHeaderAsync(input, ct);
        var dek = UnwrapDek(header, sources);
        try
        {
            await DecryptChunksAsync(input, output, dek, header, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Replaces the key slots without touching the encrypted data.</summary>
    public static async Task RewrapAsync(
        Stream input, Stream output, IReadOnlyList<IKekSource> unlockWith, IReadOnlyList<IKekSource> newSources, CancellationToken ct = default)
    {
        if (newSources.Count is 0 or > MaxSlots) throw new ArgumentException("One to eight key sources are required.", nameof(newSources));

        var header = await ReadHeaderAsync(input, ct);
        var dek = UnwrapDek(header, unlockWith);
        try
        {
            var slots = newSources.Select(s => WrapSlot(s, dek, header.Fixed)).ToList();
            await WriteHeaderAsync(output, header.Fixed, slots, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
        await input.CopyToAsync(output, ct);
    }

    // ---------- header ----------

    private static byte[] SlotAad(byte[] fixedHeader, byte type, byte[] descriptor)
    {
        var aad = new byte[fixedHeader.Length + 2 + descriptor.Length];
        fixedHeader.CopyTo(aad, 0);
        aad[fixedHeader.Length] = type;
        aad[fixedHeader.Length + 1] = (byte)descriptor.Length;
        descriptor.CopyTo(aad, fixedHeader.Length + 2);
        return aad;
    }

    private static Slot WrapSlot(IKekSource source, byte[] dek, byte[] fixedHeader)
    {
        var material = source.Create();
        try
        {
            if (material.Descriptor.Length > 255) throw new InvalidOperationException("Key descriptor too long.");
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrapped = new byte[KeySize];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(material.Kek, TagSize);
            aes.Encrypt(nonce, dek, wrapped, tag, SlotAad(fixedHeader, source.SlotType, material.Descriptor));
            return new Slot(source.SlotType, material.Descriptor, nonce, wrapped, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Kek);
        }
    }

    private static async Task WriteHeaderAsync(Stream output, byte[] fixedHeader, List<Slot> slots, CancellationToken ct)
    {
        using var block = new MemoryStream();
        block.WriteByte((byte)slots.Count);
        foreach (var slot in slots)
        {
            block.WriteByte(slot.Type);
            block.WriteByte((byte)slot.Descriptor.Length);
            block.Write(slot.Descriptor);
            block.Write(slot.Nonce);
            block.Write(slot.Wrapped);
            block.Write(slot.Tag);
        }

        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, (int)block.Length);
        await output.WriteAsync(fixedHeader, ct);
        await output.WriteAsync(length, ct);
        await output.WriteAsync(block.ToArray(), ct);
    }

    private static async Task<Header> ReadHeaderAsync(Stream input, CancellationToken ct)
    {
        var fixedHeader = new byte[FixedHeaderSize];
        var lengthBytes = new byte[4];
        try
        {
            await input.ReadExactlyAsync(fixedHeader, ct);
            if (!fixedHeader.AsSpan(0, 8).SequenceEqual(Magic))
                throw new DumpDecryptionException("This is not a ProcLens encrypted dump.", DecryptionFailure.Format);

            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.AsSpan(8));
            if (chunkSize is < MinChunkSize or > MaxChunkSize)
                throw new DumpDecryptionException("The file header is invalid (chunk size).", DecryptionFailure.Format);

            await input.ReadExactlyAsync(lengthBytes, ct);
            var slotsLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (slotsLength is < 1 or > MaxSlotsLength)
                throw new DumpDecryptionException("The file header is invalid (key slots).", DecryptionFailure.Format);

            var block = new byte[slotsLength];
            await input.ReadExactlyAsync(block, ct);
            return new Header(fixedHeader, chunkSize, ParseSlots(block));
        }
        catch (EndOfStreamException e)
        {
            throw new DumpDecryptionException("The file is too short to be a valid encrypted dump.", DecryptionFailure.Format, e);
        }
    }

    private static List<Slot> ParseSlots(byte[] block)
    {
        var count = block[0];
        if (count is 0 or > MaxSlots) throw new DumpDecryptionException("The file header is invalid (slot count).", DecryptionFailure.Format);

        var slots = new List<Slot>(count);
        var offset = 1;
        for (var i = 0; i < count; i++)
        {
            if (offset + 2 > block.Length) throw new DumpDecryptionException("The file header is invalid (slot).", DecryptionFailure.Format);
            var type = block[offset];
            var descriptorLength = block[offset + 1];
            offset += 2;
            if (offset + descriptorLength + NonceSize + KeySize + TagSize > block.Length)
                throw new DumpDecryptionException("The file header is invalid (slot length).", DecryptionFailure.Format);

            slots.Add(new Slot(
                type,
                block.AsSpan(offset, descriptorLength).ToArray(),
                block.AsSpan(offset + descriptorLength, NonceSize).ToArray(),
                block.AsSpan(offset + descriptorLength + NonceSize, KeySize).ToArray(),
                block.AsSpan(offset + descriptorLength + NonceSize + KeySize, TagSize).ToArray()));
            offset += descriptorLength + NonceSize + KeySize + TagSize;
        }

        if (offset != block.Length) throw new DumpDecryptionException("The file header is invalid (trailing data).", DecryptionFailure.Format);
        return slots;
    }

    private static byte[] UnwrapDek(Header header, IReadOnlyList<IKekSource> sources)
    {
        foreach (var slot in header.Slots)
        {
            foreach (var source in sources.Where(s => s.SlotType == slot.Type))
            {
                var kek = source.Resolve(slot.Descriptor);
                if (kek is null) continue;
                try
                {
                    var dek = new byte[KeySize];
                    using var aes = new AesGcm(kek, TagSize);
                    aes.Decrypt(slot.Nonce, slot.Wrapped, slot.Tag, dek, SlotAad(header.Fixed, slot.Type, slot.Descriptor));
                    return dek;
                }
                catch (CryptographicException)
                {
                    // wrong password or key: try the next candidate
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(kek);
                }
            }
        }

        throw new DumpDecryptionException(
            "None of the available keys could unlock this file. The password may be wrong, or the file was protected " +
            "with a key that belongs to another user or machine.", DecryptionFailure.KeyUnavailable);
    }

    // ---------- chunks ----------

    private static void SetNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, ulong index)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
    }

    private static byte[] ChunkAad(byte[] fixedHeader, ulong index, bool final)
    {
        var aad = new byte[fixedHeader.Length + 9];
        fixedHeader.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(fixedHeader.Length), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static async Task<int> ReadFullAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task EncryptChunksAsync(Stream input, Stream output, byte[] dek, byte[] fixedHeader, int chunkSize, CancellationToken ct)
    {
        using var aes = new AesGcm(dek, TagSize);
        var current = new byte[chunkSize];
        var next = new byte[chunkSize];
        var cipher = new byte[chunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        try
        {
            var currentLength = await ReadFullAsync(input, current, ct);
            ulong index = 0;
            while (true)
            {
                var nextLength = 0;
                bool final;
                if (currentLength < chunkSize)
                {
                    final = true;
                }
                else
                {
                    // Look one chunk ahead so the true last chunk can be flagged as final.
                    nextLength = await ReadFullAsync(input, next, ct);
                    final = nextLength == 0;
                }

                SetNonce(nonce, fixedHeader.AsSpan(28, 4), index);
                aes.Encrypt(nonce, current.AsSpan(0, currentLength), cipher.AsSpan(0, currentLength), tag, ChunkAad(fixedHeader, index, final));
                await output.WriteAsync(cipher.AsMemory(0, currentLength), ct);
                await output.WriteAsync(tag, ct);

                if (final) break;
                (current, next) = (next, current);
                currentLength = nextLength;
                index++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            CryptographicOperations.ZeroMemory(next);
        }
    }

    private static async Task DecryptChunksAsync(Stream input, Stream output, byte[] dek, Header header, CancellationToken ct)
    {
        var fullChunk = header.ChunkSize + TagSize;
        using var aes = new AesGcm(dek, TagSize);
        var current = new byte[fullChunk];
        var next = new byte[fullChunk];
        var plain = new byte[header.ChunkSize];
        var nonce = new byte[NonceSize];
        try
        {
            var currentLength = await ReadFullAsync(input, current, ct);
            ulong index = 0;
            while (true)
            {
                if (currentLength < TagSize)
                    throw new DumpDecryptionException("The file is truncated.", DecryptionFailure.Authentication);

                var nextLength = 0;
                bool final;
                if (currentLength < fullChunk)
                {
                    final = true;
                }
                else
                {
                    nextLength = await ReadFullAsync(input, next, ct);
                    final = nextLength == 0;
                }

                var plainLength = currentLength - TagSize;
                SetNonce(nonce, header.Fixed.AsSpan(28, 4), index);
                try
                {
                    aes.Decrypt(nonce, current.AsSpan(0, plainLength), current.AsSpan(plainLength, TagSize),
                        plain.AsSpan(0, plainLength), ChunkAad(header.Fixed, index, final));
                }
                catch (CryptographicException e)
                {
                    throw new DumpDecryptionException(
                        $"Authentication failed at chunk {index}: the file is corrupt, truncated, extended or has been modified.", DecryptionFailure.Authentication, e);
                }

                await output.WriteAsync(plain.AsMemory(0, plainLength), ct);
                if (final) break;
                (current, next) = (next, current);
                currentLength = nextLength;
                index++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(current);
            CryptographicOperations.ZeroMemory(next);
        }
    }
}
