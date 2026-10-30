using System.Security.Cryptography;
using ProcLens.Application.Abstractions;
using ProcLens.Application.Audit;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;

namespace ProcLens.Application.Dumps;

public enum VaultStatus
{
    Succeeded,
    NotFound,
    NotEncrypted,
    InvalidRequest,
    CannotUnlock,
    IntegrityFailure,
    Failed,
}

public sealed record VaultResult(VaultStatus Status, string Message, string? OutputPath = null, IReadOnlyList<string>? Details = null);

/// <summary>
/// Reading dumps back out of protection: decrypt, password-protected export, and verification. Every operation is
/// audited, and none runs if the audit trail cannot be written, because these are the moments secrets are exposed.
/// </summary>
public sealed class DumpVaultService(
    IAuditLog audit,
    IOperatorContext operatorContext,
    IKekSource? machineKey = null,
    TimeProvider? timeProvider = null) : IDumpVaultService
{
    private readonly AuditRecorder _recorder = new(audit, operatorContext, timeProvider);

    /// <summary>Writes the plaintext dump to <paramref name="outputFile"/> (new file, owner-only). The caller is then responsible for it.</summary>
    public Task<VaultResult> DecryptAsync(string file, string outputFile, SecretText? password, CancellationToken ct = default) =>
        RunAsync("dump.decrypt", file, outputFile, ct, async metadata =>
        {
            if (Precheck(file) is { } early) return early;
            if (OutputPathPolicy.Check(outputFile) is { } badPath) return Fail(VaultStatus.InvalidRequest, badPath);
            var output = Path.GetFullPath(outputFile);
            if (File.Exists(output)) return Fail(VaultStatus.InvalidRequest, $"'{output}' already exists; existing files are never overwritten.");
            if (Path.GetDirectoryName(output) is { Length: > 0 } dir) RestrictedFile.CreateDirectory(dir);

            var completed = false;
            try
            {
                string hash;
                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                await using (var target = RestrictedFile.CreateNew(output))
                {
                    await DumpCipher.DecryptAsync(input, target, Sources(password), ct);
                }
                hash = await DumpFiles.ComputeSha256Async(output, ct);

                if (metadata is not null && !string.Equals(metadata.PlaintextSha256, hash, StringComparison.OrdinalIgnoreCase))
                    return Fail(VaultStatus.IntegrityFailure, "The decrypted data does not match the hash recorded when the dump was taken.");

                completed = true;
                return new VaultResult(VaultStatus.Succeeded, "Decrypted.", output,
                    ["The output is an unprotected copy of the process's memory. Protect it and delete it securely when finished."]);
            }
            catch (DumpDecryptionException e)
            {
                return Fail(StatusOf(e), e.Message);
            }
            finally
            {
                if (!completed) RestrictedFile.SecureDelete(output);
            }
        });

    /// <summary>
    /// Writes a copy protected only by <paramref name="newPassword"/> (so it can leave this machine). The data is not
    /// re-encrypted; the original file is untouched and keeps working with its existing key.
    /// </summary>
    public Task<VaultResult> ExportAsync(
        string file, string outputFile, SecretText newPassword, SecretText? currentPassword, CancellationToken ct = default) =>
        RunAsync("dump.export", file, outputFile, ct, async metadata =>
        {
            if (Precheck(file) is { } early) return early;
            if (PasswordKekSource.CheckStrength(newPassword) is { } weak) return Fail(VaultStatus.InvalidRequest, weak);
            if (OutputPathPolicy.Check(outputFile) is { } badPath) return Fail(VaultStatus.InvalidRequest, badPath);
            var output = Path.GetFullPath(outputFile);
            if (File.Exists(output)) return Fail(VaultStatus.InvalidRequest, $"'{output}' already exists; existing files are never overwritten.");
            if (Path.GetDirectoryName(output) is { Length: > 0 } dir) RestrictedFile.CreateDirectory(dir);

            var completed = false;
            try
            {
                // Re-wrapping copies the encrypted chunks as they are, so authenticate the source first. Otherwise a
                // damaged or modified dump would be exported without anyone noticing until it is opened.
                using (var sink = new HashingSinkStream())
                {
                    await using var check = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                    await DumpCipher.DecryptAsync(check, sink, Sources(currentPassword), ct);
                    if (metadata is not null && !string.Equals(metadata.PlaintextSha256, sink.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                        return Fail(VaultStatus.IntegrityFailure, "The source does not match the hash recorded when the dump was taken, so it was not exported.");
                }

                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                await using (var target = RestrictedFile.CreateNew(output))
                {
                    await DumpCipher.RewrapAsync(input, target, Sources(currentPassword), [new PasswordKekSource(newPassword)], ct);
                }

                if (metadata is not null)
                {
                    var exported = metadata with
                    {
                        DumpFilePath = output,
                        FileSize = new FileInfo(output).Length,
                        Sha256 = await DumpFiles.ComputeSha256Async(output, ct),
                    };
                    await using var sidecar = RestrictedFile.CreateNew(DumpFiles.SidecarPath(output));
                    await sidecar.WriteAsync(System.Text.Encoding.UTF8.GetBytes(DumpFiles.SerializeMetadata(exported)), ct);
                }

                completed = true;
                return new VaultResult(VaultStatus.Succeeded, "Exported with password protection.", output,
                    ["Only the password opens this copy; the key of this machine is not part of it. Store the password separately."]);
            }
            catch (DumpDecryptionException e)
            {
                return Fail(StatusOf(e), e.Message);
            }
            finally
            {
                if (!completed)
                {
                    RestrictedFile.SecureDelete(output);
                    TryDelete(DumpFiles.SidecarPath(output));
                }
            }
        });

    /// <summary>Checks the file end to end (every chunk's authentication tag) and against the recorded hashes, without writing plaintext anywhere.</summary>
    public Task<VaultResult> VerifyAsync(string file, SecretText? password, CancellationToken ct = default) =>
        RunAsync("dump.verify", file, null, ct, async metadata =>
        {
            if (!File.Exists(file)) return Fail(VaultStatus.NotFound, $"File not found: {file}");
            var details = new List<string>();
            var ok = true;

            var stored = await DumpFiles.ComputeSha256Async(file, ct);
            if (metadata is null) details.Add("No metadata file: recorded hashes cannot be checked.");
            else if (string.Equals(metadata.Sha256, stored, StringComparison.OrdinalIgnoreCase)) details.Add("Stored file hash matches the recorded value.");
            else { ok = false; details.Add("Stored file hash does NOT match the recorded value: the file changed after it was created."); }

            if (!DumpCipher.IsEncrypted(file))
            {
                var structure = MinidumpReader.Read(file);
                details.Add(structure.IsValid ? "Minidump structure is valid." : $"Minidump structure is invalid: {structure.Problem}");
                return structure.IsValid && ok
                    ? new VaultResult(VaultStatus.Succeeded, "Verified.", Details: details)
                    : new VaultResult(VaultStatus.IntegrityFailure, "Verification failed.", Details: details);
            }

            try
            {
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                using var sink = new HashingSinkStream();
                await DumpCipher.DecryptAsync(input, sink, Sources(password), ct);
                details.Add($"All encrypted chunks authenticated ({sink.BytesWritten:N0} bytes of plaintext).");

                if (metadata is null) details.Add("No metadata file: the original dump hash cannot be compared.");
                else if (string.Equals(metadata.PlaintextSha256, sink.Sha256Hex, StringComparison.OrdinalIgnoreCase)) details.Add("Decrypted data matches the hash recorded when the dump was taken.");
                else { ok = false; details.Add("Decrypted data does NOT match the recorded original hash."); }
            }
            catch (DumpDecryptionException e)
            {
                return new VaultResult(StatusOf(e), e.Message, Details: details);
            }

            return ok
                ? new VaultResult(VaultStatus.Succeeded, "Verified.", Details: details)
                : new VaultResult(VaultStatus.IntegrityFailure, "Verification failed.", Details: details);
        });

    // ---------- helpers ----------

    private List<IKekSource> Sources(SecretText? password)
    {
        var sources = new List<IKekSource>();
        if (machineKey is not null) sources.Add(machineKey);
        if (password is not null) sources.Add(new PasswordKekSource(password));
        return sources;
    }

    private static VaultResult? Precheck(string file)
    {
        if (!File.Exists(file)) return Fail(VaultStatus.NotFound, $"File not found: {file}");
        if (!DumpCipher.IsEncrypted(file)) return Fail(VaultStatus.NotEncrypted, "The file is not an encrypted ProcLens dump.");
        return null;
    }

    private static VaultResult Fail(VaultStatus status, string message) => new(status, message);

    private static VaultStatus StatusOf(DumpDecryptionException e) => e.Kind switch
    {
        DecryptionFailure.KeyUnavailable => VaultStatus.CannotUnlock,
        DecryptionFailure.Authentication => VaultStatus.IntegrityFailure,
        _ => VaultStatus.NotEncrypted,
    };

    private async Task<VaultResult> RunAsync(
        string operation, string file, string? output, CancellationToken ct, Func<DumpMetadata?, Task<VaultResult>> action)
    {
        if (await _recorder.CheckAvailableAsync(ct) is { } problem)
            return Fail(VaultStatus.Failed, problem);

        var metadata = File.Exists(file) ? DumpFiles.TryReadMetadata(file) : null;
        VaultResult result;
        try
        {
            result = await action(metadata);
        }
        catch (OperationCanceledException)
        {
            result = Fail(VaultStatus.Failed, "Cancelled.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            result = Fail(VaultStatus.Failed, e.Message);
        }

        var outcome = result.Status switch
        {
            VaultStatus.Succeeded => AuditOutcome.Succeeded,
            VaultStatus.CannotUnlock => AuditOutcome.Denied,
            _ => AuditOutcome.Failed,
        };
        var record = _recorder.NewRecord(operation, new AuditAuthorization(result.Status != VaultStatus.CannotUnlock, "key-holder",
            "Access to the content requires the machine key or the password."), outcome) with
        {
            TargetPid = metadata?.ProcessId,
            TargetName = metadata?.ProcessName,
            TargetStartTime = metadata?.ProcessStartTime,
            DumpType = metadata?.DumpType.ToString(),
            Location = AuditRecord.Clean(output is null ? file : $"{file} -> {output}"),
            ErrorDetails = result.Status == VaultStatus.Succeeded ? null : AuditRecord.Clean(result.Message),
            FileSha256 = metadata?.Sha256,
            Encrypted = true,
        };

        return await _recorder.TryAppendAsync(record, ct) is { } warning
            ? result with { Details = [.. result.Details ?? [], warning] }
            : result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
