using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;

namespace ProcLens.Core.Dumps;

/// <param name="RawPath">The dump exactly as the operating system wrote it.</param>
/// <param name="FinalPath">Where the finished dump goes. Equal to <paramref name="RawPath"/> when it is left unprotected.</param>
/// <param name="KeySources">Empty to leave the dump unencrypted; otherwise every source gets a key slot.</param>
/// <param name="RawDumpCheck">An extra, platform specific check of the raw dump (for example DbgHelp reading it); returns a problem or null.</param>
public sealed record FinalizeRequest(
    string RawPath,
    string FinalPath,
    IReadOnlyList<IKekSource> KeySources,
    int ProcessId,
    string ProcessName,
    DateTimeOffset ProcessStartTime,
    DumpType Type,
    Func<string, string?>? RawDumpCheck = null,
    IProgress<DumpProgress>? Progress = null);

public sealed record FinalizeResult(DumpStatus Status, string? Message, DumpMetadata? Metadata)
{
    public bool Succeeded => Status == DumpStatus.Succeeded;
}

/// <summary>Turns a freshly written raw dump into the finished, verified, recorded artifact.</summary>
public interface IDumpFinalizer
{
    Task<FinalizeResult> FinalizeAsync(FinalizeRequest request, CancellationToken ct);
}

/// <summary>
/// Everything that happens after the operating system has written the dump, and none of what is specific to an
/// operating system: check it, hash it, encrypt it, prove the encrypted copy can be read back, wipe the raw copy,
/// record the metadata. On any failure or cancellation nothing is left behind (raw dump, ciphertext, metadata).
/// </summary>
public sealed class DumpFinalizer(TimeProvider? timeProvider = null) : IDumpFinalizer
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<FinalizeResult> FinalizeAsync(FinalizeRequest request, CancellationToken ct)
    {
        var sidecar = DumpFiles.SidecarPath(request.FinalPath);
        var completed = false;
        // Only files this method created may be removed on failure. A file that already existed under the final or
        // metadata name belongs to someone else (CreateNew refuses to touch it), so it must survive our failure.
        var owned = new Ownership { Final = request.FinalPath == request.RawPath };
        try
        {
            ct.ThrowIfCancellationRequested();

            if (CheckRawDump(request) is { } problem) return Failed(problem);

            var (plaintextHash, storedHash, scheme) = request.KeySources.Count == 0
                ? await LeaveUnprotectedAsync(request)
                : await ProtectAsync(request, owned, ct);

            var length = new FileInfo(request.FinalPath).Length;
            request.Progress?.Report(new DumpProgress(length));

            var metadata = new DumpMetadata(
                DumpId: Guid.NewGuid(),
                ProcessId: request.ProcessId,
                ProcessName: request.ProcessName,
                ProcessStartTime: request.ProcessStartTime,
                DumpType: request.Type,
                DumpFilePath: request.FinalPath,
                FileSize: length,
                CreationTime: _time.GetUtcNow(),
                OperatingSystem: RuntimeInformation.OSDescription,
                ApplicationVersion: ApplicationVersion(),
                CollectionStatus: CollectionStatus.Succeeded,
                ErrorDetails: null,
                Sha256: storedHash,
                PlaintextSha256: plaintextHash,
                EncryptionScheme: scheme);

            await using (var meta = RestrictedFile.CreateNew(sidecar))
            {
                owned.Sidecar = true;
                await meta.WriteAsync(Encoding.UTF8.GetBytes(DumpFiles.SerializeMetadata(metadata)), CancellationToken.None);
            }

            completed = true;
            return new FinalizeResult(DumpStatus.Succeeded, "Dump created.", metadata);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return Failed($"Could not write the dump file: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            return new FinalizeResult(DumpStatus.AccessDenied, $"The output location is not writable: {e.Message}", null);
        }
        catch (Exception e) when (e is CryptographicException or DumpDecryptionException or InvalidOperationException)
        {
            return Failed($"Encrypting the dump failed: {e.Message}");
        }
        finally
        {
            if (!completed)
            {
                // Never leave a partial dump, temporary plaintext or metadata behind (also on cancellation).
                RestrictedFile.SecureDelete(request.RawPath);
                if (owned.Final) TryDelete(request.FinalPath);
                if (owned.Sidecar) TryDelete(sidecar);
            }
        }
    }

    private sealed class Ownership
    {
        public bool Final;
        public bool Sidecar;
    }

    /// <summary>Non-empty, structurally valid, and (where the platform offers it) readable by the platform's own library.</summary>
    private static string? CheckRawDump(FinalizeRequest request)
    {
        if (new FileInfo(request.RawPath).Length == 0) return "The dump file is empty.";

        var structure = MinidumpReader.Read(request.RawPath);
        if (!structure.IsValid) return $"The dump failed verification: {structure.Problem}";

        return request.RawDumpCheck?.Invoke(request.RawPath) is { } platformProblem
            ? $"The dump failed verification: {platformProblem}"
            : null;
    }

    private static async Task<(string Plaintext, string Stored, string? Scheme)> LeaveUnprotectedAsync(FinalizeRequest request)
    {
        var hash = await DumpFiles.ComputeSha256Async(request.RawPath, CancellationToken.None);
        return (hash, hash, null);
    }

    /// <summary>
    /// One pass over the data hashes the plaintext, encrypts it and hashes the ciphertext as it is written. Then the
    /// result is decrypted again with every key source and compared, and only after that is the raw dump wiped, so an
    /// encrypted file that cannot be opened never replaces the only good copy.
    /// </summary>
    private static async Task<(string Plaintext, string Stored, string? Scheme)> ProtectAsync(FinalizeRequest request, Ownership owned, CancellationToken ct)
    {
        string plaintextHash, storedHash;
        await using (var raw = new FileStream(request.RawPath, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, useAsync: true))
        await using (var encrypted = RestrictedFile.CreateNew(request.FinalPath))
        {
            owned.Final = true;
            using var hashedRaw = new HashingReadStream(raw);
            using var hashedEncrypted = new HashingWriteStream(encrypted);
            await DumpCipher.EncryptAsync(hashedRaw, hashedEncrypted, request.KeySources, DumpCipher.DefaultChunkSize, ct);
            await hashedEncrypted.FlushAsync(ct);
            plaintextHash = hashedRaw.Sha256Hex;
            storedHash = hashedEncrypted.Sha256Hex;
        }

        foreach (var source in request.KeySources)
        {
            await using var check = new FileStream(request.FinalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            using var sink = new HashingSinkStream();
            await DumpCipher.DecryptAsync(check, sink, [source], ct);
            if (sink.Sha256Hex != plaintextHash)
                throw new InvalidDataException("The encrypted dump did not decrypt back to the original data.");
        }

        RestrictedFile.SecureDelete(request.RawPath);
        if (File.Exists(request.RawPath))
            throw new IOException("The temporary plaintext dump could not be removed.");

        return (plaintextHash, storedHash, DumpCipher.SchemeName);
    }

    private static FinalizeResult Failed(string message) => new(DumpStatus.Failed, message, null);

    private static string ApplicationVersion() =>
        (typeof(DumpFinalizer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown").Split('+')[0];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nothing more can be done */ }
    }
}
