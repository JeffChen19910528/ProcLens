// In-process measurements of the parts of ProcLens whose cost grows with data: the dump cipher, the audit log and
// the retention scan. Run in Release, nothing else heavy running:
//
//     dotnet run -c Release tools/perf/inproc.cs
//     dotnet run -c Release tools/perf/inproc.cs -- cipher        (or: audit, retention)
//
// Numbers are for the machine they run on; compare runs on the SAME machine. See docs/PERFORMANCE.md.

#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../src/ProcLens.Core
#:project ../../src/ProcLens.Application
#:project ../../src/ProcLens.Infrastructure

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ProcLens.Application.Dumps;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Infrastructure.Audit;

var which = args.FirstOrDefault() ?? "all";
Console.WriteLine($"{RuntimeInformation.OSDescription} | {RuntimeInformation.FrameworkDescription} | {Environment.ProcessorCount} logical processors");
#if DEBUG
Console.WriteLine("WARNING: this is a Debug build; the numbers are not meaningful. Use -c Release.");
#else
Console.WriteLine("Release build");
#endif
Console.WriteLine();

if (which is "all" or "cipher") Cipher();
if (which is "all" or "audit") await Audit();
if (which is "all" or "retention") await Retention();
return 0;

// ------------------------------------------------------------------------------------------------------------

static string Mb(double bytes) => $"{bytes / 1048576.0:N0} MB";
static string Rate(long bytes, TimeSpan t) => $"{bytes / 1048576.0 / t.TotalSeconds,8:N0} MB/s";

static void Cipher()
{
    Console.WriteLine("=== Cipher: AES-256-GCM, chunked, streamed (no disk) ===");
    const long size = 512L << 20;                       // 512 MB per measurement
    var key = new FixedKey();

    // Baselines on the same data, to see what the format itself costs.
    var data = RandomNumberGenerator.GetBytes(1 << 20);
    var watch = Stopwatch.StartNew();
    using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        for (var i = 0; i < 512; i++) sha.AppendData(data);
    Console.WriteLine($"  baseline SHA-256 over 512 MB:                       {Rate(size, watch.Elapsed)}");

    using (var gcm = new AesGcm(RandomNumberGenerator.GetBytes(32), 16))
    {
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        var nonce = new byte[12];
        watch.Restart();
        for (var i = 0; i < 512; i++) { BitConverter.TryWriteBytes(nonce, i); gcm.Encrypt(nonce, data, cipher, tag); }
        Console.WriteLine($"  baseline raw AES-GCM encrypt (1 MiB calls):        {Rate(size, watch.Elapsed)}");
    }

    Console.WriteLine("  chunk size      encrypt            decrypt+authenticate");
    foreach (var chunk in new[] { 4 << 10, 64 << 10, 256 << 10, 1 << 20, 4 << 20, 16 << 20 })
    {
        using var encrypted = new MemoryStream(capacity: (int)(size + size / chunk * 16 + 4096));
        watch.Restart();
        DumpCipher.EncryptAsync(new SyntheticStream(size), encrypted, [key], chunk).GetAwaiter().GetResult();
        var encryptTime = watch.Elapsed;

        encrypted.Position = 0;
        using var sink = new HashingSinkStream();
        watch.Restart();
        DumpCipher.DecryptAsync(encrypted, sink, [key]).GetAwaiter().GetResult();
        var decryptTime = watch.Elapsed;

        var label = chunk >= 1 << 20 ? $"{chunk >> 20} MiB" : $"{chunk >> 10} KiB";
        var marker = chunk == DumpCipher.DefaultChunkSize ? "  <- default" : "";
        Console.WriteLine($"  {label,-10} {Rate(size, encryptTime)}    {Rate(size, decryptTime)}{marker}");
    }

    // Does memory depend on the size of the dump? Stream to a discarding sink so the output is not retained.
    Console.WriteLine();
    Console.WriteLine("  memory vs. dump size (streaming to a discarding sink, default chunk size):");
    foreach (var gb in new[] { 1, 4, 16 })
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        watch.Restart();
        using var sink = new HashingSinkStream();
        DumpCipher.EncryptAsync(new SyntheticStream((long)gb << 30), sink, [key]).GetAwaiter().GetResult();
        var t = watch.Elapsed;
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        Console.WriteLine($"    {gb,2} GiB dump: {Rate((long)gb << 30, t)}   total managed allocation {Mb(allocated)}   (the process peak is measured end to end in e2e.cs; it is not meaningful here)");
    }
    Console.WriteLine();
}

static AuditRecord Record(int i) => new(
    Guid.NewGuid(), DateTimeOffset.UtcNow, "DOMAIN\\operator", false, "HOST", "dump.create", 1000 + i % 5000, "someprocess",
    DateTimeOffset.UtcNow, "DOMAIN\\operator", "Heap", "C:\\dumps\\someprocess_1234_heap_20260101T000000Z_abcdef12.dmp.enc",
    new AuditAuthorization(true, "owner", "The operator owns the target process."), AuditOutcome.Succeeded, null,
    new string('a', 64), true, null);

static async Task Audit()
{
    Console.WriteLine("=== Audit log ===");
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-perf-audit-" + Guid.NewGuid().ToString("N"))).FullName;
    try
    {
        // Build big logs directly (no flush-to-disk per record) so the test is about reading, not about creating.
        foreach (var count in new[] { 1_000, 10_000, 100_000 })
        {
            var path = Path.Combine(dir, $"audit-{count}.jsonl");
            var previous = AuditChain.Genesis;
            using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 1 << 16))
                for (var i = 0; i < count; i++)
                {
                    var sealedRecord = AuditChain.Seal(Record(i), previous);
                    w.Write(AuditChain.ToLine(sealedRecord)); w.Write('\n');
                    previous = sealedRecord.Hash;
                }

            var log = new FileAuditLog(path);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            var verification = log.Verify();
            var verifyTime = watch.Elapsed;
            var verifyAllocated = GC.GetTotalAllocatedBytes(true) - allocated;

            allocated = GC.GetTotalAllocatedBytes(true);
            watch.Restart();
            var last = log.ReadLast(20);                          // what 'audit show' does
            var showTime = watch.Elapsed;
            var showAllocated = GC.GetTotalAllocatedBytes(true) - allocated;

            // appending to a log of this size: should not depend on the size
            watch.Restart();
            const int appends = 50;
            for (var i = 0; i < appends; i++) await log.AppendAsync(Record(i));
            var appendEach = watch.Elapsed / appends;

            Console.WriteLine($"  {count,7:N0} records ({Mb(new FileInfo(path).Length),6}): verify {verifyTime.TotalMilliseconds,7:N0} ms, allocates {Mb(verifyAllocated),7}"
                              + $" | show --last 20 {showTime.TotalMilliseconds,7:N0} ms, allocates {Mb(showAllocated),7}"
                              + $" | one append {appendEach.TotalMilliseconds,5:N1} ms (flushed to disk) | ok={verification.Ok}, shown={last.Count}");
        }
    }
    finally { Directory.Delete(dir, true); }
    Console.WriteLine();
}

static async Task Retention()
{
    Console.WriteLine("=== Retention scan (dump prune, dry run) ===");
    foreach (var count in new[] { 500, 5_000, 20_000 })
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-perf-ret-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var path = Path.Combine(dir, $"p_{i}_mini_20260101T000000Z_{i:x8}.dmp.enc");
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                var created = DateTimeOffset.UtcNow.AddDays(-(i % 90));
                File.WriteAllText(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(new DumpMetadata(
                    Guid.NewGuid(), i, "p", created.AddMinutes(-1), DumpType.Mini, path, 4, created, "OS", "1",
                    CollectionStatus.Succeeded, null, "a", "b", DumpCipher.SchemeName)));
            }

            var service = new DumpRetentionService(new StubAudit(), new StubOperator());
            var policy = new RetentionPolicy(MaxAgeDays: 30);

            // The first scan reads files that were only just written, so real-time antivirus scans each one as it is
            // opened for the first time (about 3.6 ms per file on the development machine). That cost belongs to the
            // operating system, not to ProcLens, and does not recur for files that have been scanned already.
            var watch = Stopwatch.StartNew();
            await service.RunAsync(dir, policy, delete: false);
            var cold = watch.Elapsed;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetTotalAllocatedBytes(true);
            watch.Restart();
            var report = await service.RunAsync(dir, policy, delete: false);
            var warm = watch.Elapsed;
            Console.WriteLine($"  {count,6:N0} dumps ({count * 2,6:N0} files): first scan {cold.TotalMilliseconds,8:N0} ms | warm scan {warm.TotalMilliseconds,6:N0} ms ({warm.TotalMilliseconds / count * 1000,4:N0} us per dump), allocates {Mb(GC.GetTotalAllocatedBytes(true) - allocated),6}, selected {report.Items.Count:N0}");
        }
        finally { Directory.Delete(dir, true); }
    }
    Console.WriteLine();
}

// ------------------------------------------------------------------------------------------------------------
// Types come last in a file-based program.

/// <summary>A readable stream of <paramref name="length"/> bytes that costs no disk and little CPU.</summary>
sealed class SyntheticStream(long length) : Stream
{
    private readonly byte[] _block = RandomNumberGenerator.GetBytes(1 << 20);
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var n = (int)Math.Min(buffer.Length, length - _position);
        for (var done = 0; done < n;)
        {
            var take = Math.Min(n - done, _block.Length);
            _block.AsSpan(0, take).CopyTo(buffer[done..]);
            done += take;
        }
        _position += n;
        return n;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class FixedKey : IKekSource
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public byte SlotType => 1;
    public KekMaterial Create() => new([1], (byte[])_key.Clone());
    public byte[]? Resolve(ReadOnlySpan<byte> d) => (byte[])_key.Clone();
}

sealed class StubAudit : IAuditLog
{
    public Task EnsureAvailableAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task AppendAsync(AuditRecord record, CancellationToken ct = default) => Task.CompletedTask;
}

sealed class StubOperator : IOperatorContext
{
    public string Identity => "bench";
    public bool IsElevated => false;
    public string Machine => "bench";
}
