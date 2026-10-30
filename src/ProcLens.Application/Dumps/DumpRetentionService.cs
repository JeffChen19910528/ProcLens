using System.Text.RegularExpressions;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Audit;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;

namespace ProcLens.Application.Dumps;

public enum RetentionItemStatus
{
    /// <summary>Selected by the policy; nothing was removed (dry run).</summary>
    Planned,
    Deleted,
    Failed,
}

public sealed record RetentionItem(
    string Path,
    string Reason,
    long Bytes,
    DateTimeOffset? Created,
    string? ProcessName,
    int? ProcessId,
    bool Encrypted,
    RetentionItemStatus Status,
    string? Error = null);

/// <param name="Problem">Set when the directory could not be processed at all.</param>
public sealed record RetentionReport(
    string Directory,
    bool DryRun,
    int Kept,
    IReadOnlyList<RetentionItem> Items,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings,
    string? Problem = null)
{
    public long BytesFreed => Items.Where(i => i.Status == RetentionItemStatus.Deleted).Sum(i => i.Bytes);
    public long BytesSelected => Items.Sum(i => i.Bytes);
    public bool HasFailures => Items.Any(i => i.Status == RetentionItemStatus.Failed);
}

/// <summary>
/// Applies a retention policy to a dump directory. It only ever touches dumps that ProcLens created (a valid
/// metadata file next to the dump whose name matches) and leftover temporary plaintext from interrupted dumps. Every
/// removal is audited, and nothing is removed if the audit trail cannot be written.
/// </summary>
public sealed partial class DumpRetentionService(
    IAuditLog audit, IOperatorContext operatorContext, TimeProvider? timeProvider = null) : IDumpRetentionService
{
    /// <summary>A temporary plaintext younger than this may belong to a dump that is still being written.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly AuditRecorder _recorder = new(audit, operatorContext, timeProvider);

    [GeneratedRegex(@"^.+_\d+_(mini|heap|full)_\d{8}T\d{6}Z_[0-9a-f]{8}\.dmp\.plain\.tmp$")]
    private static partial Regex TemporaryPlaintextName();

    public async Task<RetentionReport> RunAsync(string directory, RetentionPolicy policy, bool delete, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(directory);
        RetentionReport Problem(string text) => new(full, !delete, 0, [], [], [], text);

        if (!Directory.Exists(full)) return Problem($"Directory not found: {full}");
        if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
            return Problem($"'{full}' is a symbolic link or junction; point at the real directory.");

        if (delete && await _recorder.CheckAvailableAsync(ct) is { } auditProblem)
            return Problem(auditProblem);

        var skipped = new List<string>();
        var managed = Scan(full, skipped);
        var now = _time.GetUtcNow();
        var candidates = RetentionPlanner.Plan(managed, policy, now);

        var items = candidates.Select(c => new PlannedRemoval(
            c.Dump.DumpPath, c.Dump.SidecarPath, c.Reason, c.Dump.Bytes, c.Dump.Metadata, DumpCipher.IsEncrypted(c.Dump.DumpPath))).ToList();
        items.AddRange(FindAbandonedPlaintext(full, now, skipped));

        var warnings = new List<string>();
        var results = new List<RetentionItem>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!delete)
            {
                results.Add(item.ToReport(RetentionItemStatus.Planned, null));
                continue;
            }

            var error = TryRemove(item);
            results.Add(item.ToReport(error is null ? RetentionItemStatus.Deleted : RetentionItemStatus.Failed, error));
            if (await RecordAsync(item, error, ct) is { } warning) warnings.Add(warning);
        }

        return new RetentionReport(full, !delete, managed.Count - candidates.Count, results, skipped, warnings);
    }

    private static List<ManagedDump> Scan(string directory, List<string> skipped)
    {
        var managed = new List<ManagedDump>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            var isSidecar = name.EndsWith(".dmp.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmp.enc.json", StringComparison.OrdinalIgnoreCase);
            if (!isSidecar) continue;

            var dump = file[..^DumpFiles.SidecarSuffix.Length];
            if (IsReparsePoint(file) || IsReparsePoint(dump))
            {
                skipped.Add($"{name}: symbolic links are never followed or removed.");
                continue;
            }
            if (!File.Exists(dump))
            {
                skipped.Add($"{name}: the dump it describes is missing.");
                continue;
            }

            var metadata = DumpFiles.TryReadMetadata(dump);
            if (metadata is null)
            {
                skipped.Add($"{name}: not valid ProcLens metadata, so the dump is not managed.");
                continue;
            }
            if (!string.Equals(Path.GetFileName(metadata.DumpFilePath), Path.GetFileName(dump), StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{name}: its metadata describes a different file, so it is not managed.");
                continue;
            }

            managed.Add(new ManagedDump(dump, file, metadata, new FileInfo(dump).Length + new FileInfo(file).Length));
        }
        return managed;
    }

    private static IEnumerable<PlannedRemoval> FindAbandonedPlaintext(string directory, DateTimeOffset now, List<string> skipped)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.dmp.plain.tmp"))
        {
            var name = Path.GetFileName(file);
            if (!TemporaryPlaintextName().IsMatch(name) || IsReparsePoint(file)) continue;

            var info = new FileInfo(file);
            if (now - info.LastWriteTimeUtc < AbandonedAfter)
            {
                skipped.Add($"{name}: recent temporary file, possibly from a dump still in progress.");
                continue;
            }

            yield return new PlannedRemoval(file, null, "abandoned temporary plaintext from an interrupted dump", info.Length, null, false);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string? TryRemove(PlannedRemoval item)
    {
        try
        {
            // Encrypted dumps are ciphertext; removing the file is enough. Anything holding raw memory is overwritten first.
            if (item.Encrypted) File.Delete(item.DumpPath);
            else RestrictedFile.SecureDelete(item.DumpPath);

            if (File.Exists(item.DumpPath)) return "The file could not be removed (it may be in use).";
            if (item.SidecarPath is not null) File.Delete(item.SidecarPath);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    private async Task<string?> RecordAsync(PlannedRemoval item, string? error, CancellationToken ct)
    {
        var record = _recorder.NewRecord("dump.delete",
            new AuditAuthorization(true, "retention", AuditRecord.Clean(item.Reason) ?? ""),
            error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed) with
        {
            TargetPid = item.Metadata?.ProcessId,
            TargetName = item.Metadata?.ProcessName,
            TargetStartTime = item.Metadata?.ProcessStartTime,
            DumpType = item.Metadata?.DumpType.ToString(),
            Location = AuditRecord.Clean(item.DumpPath),
            ErrorDetails = AuditRecord.Clean(error),
            FileSha256 = item.Metadata?.Sha256,
            Encrypted = item.Encrypted,
        };
        return await _recorder.TryAppendAsync(record, ct);
    }

    private sealed record PlannedRemoval(
        string DumpPath, string? SidecarPath, string Reason, long Bytes, DumpMetadata? Metadata, bool Encrypted)
    {
        public RetentionItem ToReport(RetentionItemStatus status, string? error) => new(
            DumpPath, Reason, Bytes, Metadata?.CreationTime, Metadata?.ProcessName, Metadata?.ProcessId, Encrypted, status, error);
    }
}
