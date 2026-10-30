using System.Security.Cryptography;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Application.Tests.Support;

internal sealed class InMemoryAuditLog : IAuditLog
{
    public List<AuditRecord> Records { get; } = [];
    public bool Unavailable { get; set; }
    public bool FailOnAppend { get; set; }

    public Task EnsureAvailableAsync(CancellationToken ct = default) =>
        Unavailable ? throw new IOException("audit disk is full") : Task.CompletedTask;

    public Task AppendAsync(AuditRecord record, CancellationToken ct = default)
    {
        if (Unavailable || FailOnAppend) throw new IOException("audit disk is full");
        Records.Add(AuditChain.Seal(record, Records.LastOrDefault()?.Hash ?? AuditChain.Genesis));
        return Task.CompletedTask;
    }

    /// <summary>Everything an auditor could read back, as one string, for "must not contain" assertions.</summary>
    public string AsText() => string.Join("\n", Records.Select(AuditChain.ToLine));
}

internal sealed class FakeOperator(bool elevated = false, string identity = @"PC\alice") : IOperatorContext
{
    public string Identity => identity;
    public bool IsElevated => elevated;
    public string Machine => "PC";
}

/// <summary>Stands in for the DPAPI machine key.</summary>
internal sealed class FixedKeySource(byte seed = 1) : IKekSource
{
    private readonly byte[] _key = Enumerable.Repeat(seed, 32).ToArray();
    public byte SlotType => 1;
    public KekMaterial Create() => new([0xAA], (byte[])_key.Clone());
    public byte[]? Resolve(ReadOnlySpan<byte> descriptor) => descriptor.SequenceEqual(new byte[] { 0xAA }) ? (byte[])_key.Clone() : null;
}

internal static class Crypto
{
    public static async Task<(string Path, byte[] Plain, DumpMetadata Metadata)> CreateEncryptedDumpAsync(
        string directory, IReadOnlyList<IKekSource> sources, int length = 10_000)
    {
        var plain = RandomNumberGenerator.GetBytes(length);
        var path = Path.Combine(directory, $"demo_{Guid.NewGuid():N}.dmp.enc");

        await using (var output = File.Create(path))
            await DumpCipher.EncryptAsync(new MemoryStream(plain), output, sources, 4096);

        var metadata = new DumpMetadata(
            Guid.NewGuid(), 42, "demo", DateTimeOffset.UtcNow.AddMinutes(-5), ProcLens.Core.Abstractions.DumpType.Heap, path,
            new FileInfo(path).Length, DateTimeOffset.UtcNow, "TestOS", "1.0", CollectionStatus.Succeeded, null,
            await DumpFiles.ComputeSha256Async(path), Convert.ToHexStringLower(SHA256.HashData(plain)), DumpCipher.SchemeName);
        await File.WriteAllTextAsync(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(metadata));
        return (path, plain, metadata);
    }
}
