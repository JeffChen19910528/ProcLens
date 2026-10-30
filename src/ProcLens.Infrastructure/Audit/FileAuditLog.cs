using System.Text;
using ProcLens.Core.Audit;
using ProcLens.Core.Security;

namespace ProcLens.Infrastructure.Audit;

/// <summary>
/// Append-only JSON Lines audit trail with a hash chain. The file is created readable only by the current user.
/// Appends take an exclusive lock so several ProcLens processes cannot interleave or fork the chain.
/// </summary>
public sealed class FileAuditLog(string path) : IAuditLog
{
    // Waiting is cheap, while giving up means a privileged operation is refused (or its record lost), so be patient: up to ~10 s.
    private const int LockAttempts = 400;
    private const int TailBytes = 64 * 1024;

    public string Path { get; } = path;

    public async Task EnsureAvailableAsync(CancellationToken ct = default)
    {
        await using var stream = await OpenExclusiveAsync(ct);
    }

    public async Task AppendAsync(AuditRecord record, CancellationToken ct = default)
    {
        await using var stream = await OpenExclusiveAsync(ct);

        var sealedRecord = AuditChain.Seal(record, LastHash(stream));
        var line = Encoding.UTF8.GetBytes(AuditChain.ToLine(sealedRecord) + "\n");

        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);
        stream.Flush(flushToDisk: true);
    }

    public IReadOnlyList<AuditRecord> ReadAll() =>
        ReadLines().Select(AuditChain.Parse).Where(r => r is not null).Select(r => r!).ToList();

    /// <summary>The newest <paramref name="count"/> records. Memory use does not depend on the size of the log.</summary>
    public IReadOnlyList<AuditRecord> ReadLast(int count)
    {
        var tail = new Queue<string>(Math.Max(1, count));
        foreach (var line in ReadLines())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (tail.Count == count) tail.Dequeue();
            tail.Enqueue(line);
        }
        return tail.Select(AuditChain.Parse).Where(r => r is not null).Select(r => r!).ToList();
    }

    public AuditVerification Verify(string? anchor = null) => AuditChain.Verify(ReadLines(), anchor);

    /// <summary>Streams the file one line at a time; a large log is never held in memory.</summary>
    private IEnumerable<string> ReadLines()
    {
        if (!File.Exists(Path)) yield break;
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private async Task<FileStream> OpenExclusiveAsync(CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory)) RestrictedFile.CreateDirectory(directory);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return RestrictedFile.OpenOrCreate(Path, FileShare.None);
            }
            catch (IOException) when (attempt < LockAttempts && File.Exists(Path))
            {
                await Task.Delay(Random.Shared.Next(10, 40), ct); // another ProcLens process is appending; jitter avoids lock-step retries
            }
        }
    }

    private static string LastHash(FileStream stream)
    {
        if (stream.Length == 0) return AuditChain.Genesis;

        var length = (int)Math.Min(stream.Length, TailBytes);
        var buffer = new byte[length];
        stream.Seek(-length, SeekOrigin.End);
        stream.ReadExactly(buffer);

        var last = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        // An unreadable tail leaves a value that matches nothing, so verification reports the break.
        return last is null ? AuditChain.Genesis : AuditChain.Parse(last)?.Hash ?? "unreadable-previous-record";
    }
}
