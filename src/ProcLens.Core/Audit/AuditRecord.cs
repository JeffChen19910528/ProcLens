using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcLens.Core.Audit;

public enum AuditOutcome
{
    Succeeded,
    Denied,
    Failed,
    Cancelled,
}

public sealed record AuditAuthorization(bool Allowed, string Policy, string Detail);

/// <summary>
/// One entry of the audit trail. By construction it holds no secrets: there are no fields for passwords, keys or
/// memory contents, free text is length-limited, and callers pass only metadata about the operation.
/// </summary>
public sealed record AuditRecord(
    Guid Id,
    DateTimeOffset Timestamp,
    string Operator,
    bool OperatorElevated,
    string Machine,
    string Operation,
    int? TargetPid,
    string? TargetName,
    DateTimeOffset? TargetStartTime,
    string? TargetOwner,
    string? DumpType,
    string? Location,
    AuditAuthorization Authorization,
    AuditOutcome Outcome,
    string? ErrorDetails,
    string? FileSha256,
    bool? Encrypted,
    string? Justification,
    int Version = 1,
    string PrevHash = "",
    string Hash = "")
{
    public const int MaxText = 500;

    /// <summary>Limits free text and strips control characters so one entry can never span or forge lines.</summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var cleaned = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return cleaned.Length <= MaxText ? cleaned : cleaned[..MaxText] + "…";
    }
}

/// <param name="HeadHash">
/// The hash of the newest record. Store it somewhere the log's writer cannot reach; passing it back as an anchor later
/// proves that nothing before it was rewritten and nothing was cut off after it.
/// </param>
public sealed record AuditVerification(bool Ok, int Records, int? BrokenAtLine, string? Problem, string? HeadHash = null);

/// <summary>
/// Tamper-evident hash chain: each record stores the hash of its predecessor and its own hash over its content.
/// Editing, inserting or deleting a record anywhere but the very end breaks the chain at that point. Removing
/// records from the end cannot be detected from the file alone.
///
/// The hashes are not keyed, so they detect damage and careless tampering, not a determined attacker: whoever can write
/// the file can rebuild the whole chain consistently. The defence is an external anchor: record the head hash
/// elsewhere and verify against it later (see <c>Verify(lines, anchor)</c>).
/// </summary>
public static class AuditChain
{
    public static readonly string Genesis = new('0', 64);

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AuditRecord Seal(AuditRecord record, string previousHash)
    {
        var unsealed = record with { PrevHash = previousHash, Hash = "" };
        return unsealed with { Hash = Compute(unsealed) };
    }

    public static string ToLine(AuditRecord sealedRecord) => JsonSerializer.Serialize(sealedRecord, Json);

    public static AuditRecord? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<AuditRecord>(line, Json); }
        catch (JsonException) { return null; }
    }

    private static string Compute(AuditRecord unsealed) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unsealed, Json))));

    public static AuditVerification Verify(IEnumerable<string> lines, string? anchor = null)
    {
        var previous = Genesis;
        var count = 0;
        var lineNumber = 0;
        var anchorSeen = anchor is null;
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var record = Parse(line);
            if (record is null) return new(false, count, lineNumber, "The line is not a valid audit record.");
            if (record.PrevHash != previous) return new(false, count, lineNumber, "The record does not follow the previous one (a record was removed, inserted or reordered).");
            if (record.Hash != Compute(record with { Hash = "" })) return new(false, count, lineNumber, "The record's content does not match its hash (it was modified).");

            previous = record.Hash;
            count++;
            if (anchor is not null && string.Equals(record.Hash, anchor, StringComparison.OrdinalIgnoreCase)) anchorSeen = true;
        }

        var head = previous == Genesis ? null : previous;
        if (!anchorSeen)
            return new(false, count, null, "The anchor hash is not in the log: records before it were rewritten, or the log was cut short.", head);
        return new(true, count, null, null, head);
    }
}

public interface IAuditLog
{
    /// <summary>Throws if records cannot currently be written. Privileged operations call this first and refuse to run otherwise.</summary>
    Task EnsureAvailableAsync(CancellationToken ct = default);

    Task AppendAsync(AuditRecord record, CancellationToken ct = default);
}
