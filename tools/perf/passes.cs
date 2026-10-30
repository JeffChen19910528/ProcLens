// Breaks the post-processing of an encrypted dump into its passes over the data, on a real file of the given size:
// the cost of each step that follows MiniDumpWriteDump. Used to decide which steps are worth merging.
//
//     dotnet run -c Release tools/perf/passes.cs -- 1200        (size in MB; needs about 3x that in free disk space)

#:property PublishAot=false
#:project ../../src/ProcLens.Core

using System.Diagnostics;
using System.Security.Cryptography;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;

var mb = args.Length > 0 ? int.Parse(args[0]) : 1200;
var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-perf-passes-" + Guid.NewGuid().ToString("N"))).FullName;
var plain = Path.Combine(dir, "dump.dmp");
var encrypted = Path.Combine(dir, "dump.dmp.enc");
try
{
    var block = new byte[1 << 20];
    Random.Shared.NextBytes(block);
    using (var f = new FileStream(plain, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        for (var i = 0; i < mb; i++) f.Write(block);

    var key = new Key();
    long bytes = (long)mb << 20;
    Console.WriteLine($"{mb} MB dump, steps that run after MiniDumpWriteDump (file system cache is warm, as in the real run):\n");

    Step("SHA-256 of the plaintext file", () => DumpFiles.ComputeSha256Async(plain).GetAwaiter().GetResult());
    Step("encrypt plaintext file -> encrypted file", () =>
    {
        using var input = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, true);
        using var output = RestrictedFile.CreateNew(encrypted);
        DumpCipher.EncryptAsync(input, output, [key]).GetAwaiter().GetResult();
    });
    Step("decrypt encrypted file -> hash (read-back proof)", () =>
    {
        using var input = new FileStream(encrypted, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        using var sink = new HashingSinkStream();
        DumpCipher.DecryptAsync(input, sink, [key]).GetAwaiter().GetResult();
    });
    Step("SHA-256 of the encrypted file (stored hash)", () => DumpFiles.ComputeSha256Async(encrypted).GetAwaiter().GetResult());
    Step("overwrite plaintext with zeros, then delete", () => RestrictedFile.SecureDelete(plain));

    // What merging would save: hash while encrypting (input side) and hash while writing the ciphertext (output side).
    File.Delete(encrypted);
    using (var f = new FileStream(plain, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        for (var i = 0; i < mb; i++) f.Write(block);
    Console.WriteLine();
    Step("MERGED: encrypt while hashing input and output", () =>
    {
        using var input = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, true);
        using var hashedInput = new HashingReadStream(input);
        using var output = RestrictedFile.CreateNew(encrypted);
        using var hashedOutput = new HashingWriteStream(output);
        DumpCipher.EncryptAsync(hashedInput, hashedOutput, [key]).GetAwaiter().GetResult();
        _ = hashedInput.Hex; _ = hashedOutput.Hex;
    });

    void Step(string what, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        Console.WriteLine($"  {what,-52} {watch.Elapsed.TotalSeconds,6:N2} s   {bytes / 1048576.0 / watch.Elapsed.TotalSeconds,7:N0} MB/s");
    }
}
finally { try { Directory.Delete(dir, true); } catch (IOException) { } }

sealed class Key : IKekSource
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public byte SlotType => 1;
    public KekMaterial Create() => new([1], (byte[])_key.Clone());
    public byte[]? Resolve(ReadOnlySpan<byte> d) => (byte[])_key.Clone();
}

/// <summary>Hashes everything read through it.</summary>
sealed class HashingReadStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public string Hex => Convert.ToHexStringLower(_hash.GetHashAndReset());
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        _hash.AppendData(buffer.Span[..n]);
        return n;
    }
    public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); _hash.AppendData(buffer, offset, n); return n; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Hashes everything written through it.</summary>
sealed class HashingWriteStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public string Hex => Convert.ToHexStringLower(_hash.GetHashAndReset());
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _hash.AppendData(buffer.Span);
        await inner.WriteAsync(buffer, ct);
    }
    public override void Write(byte[] buffer, int offset, int count) { _hash.AppendData(buffer, offset, count); inner.Write(buffer, offset, count); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
