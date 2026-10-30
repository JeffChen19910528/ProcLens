using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcLens.Core.Abstractions;

namespace ProcLens.Core.Dumps;

public enum CollectionStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <remarks>
/// <c>Sha256</c> is the hash of the file as stored (the ciphertext when encrypted); <c>PlaintextSha256</c> is the hash of
/// the original dump. Hashes are integrity checks against corruption or tampering, not a form of protection.
/// </remarks>
public sealed record DumpMetadata(
    Guid DumpId,
    int ProcessId,
    string ProcessName,
    DateTimeOffset ProcessStartTime,
    DumpType DumpType,
    string DumpFilePath,
    long FileSize,
    DateTimeOffset CreationTime,
    string OperatingSystem,
    string ApplicationVersion,
    CollectionStatus CollectionStatus,
    string? ErrorDetails,
    string Sha256,
    string PlaintextSha256,
    string? EncryptionScheme);

public static class DumpFiles
{
    public const string SidecarSuffix = ".json";

    /// <summary>Real metadata is about 1 KB. Anything larger is not read: it can only be hostile or damaged.</summary>
    public const long MaxMetadataBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SidecarPath(string dumpPath) => dumpPath + SidecarSuffix;

    public static string SerializeMetadata(DumpMetadata metadata) => JsonSerializer.Serialize(metadata, Options);

    /// <returns>Null if the file is absent or is not valid metadata.</returns>
    public static DumpMetadata? TryReadMetadata(string dumpPath)
    {
        try
        {
            var sidecar = SidecarPath(dumpPath);
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length > MaxMetadataBytes) return null;
            return File.Exists(sidecar) ? JsonSerializer.Deserialize<DumpMetadata>(File.ReadAllText(sidecar), Options) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
