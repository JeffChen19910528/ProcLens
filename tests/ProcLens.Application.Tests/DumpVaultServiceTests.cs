using System.Security.Cryptography;
using FluentAssertions;
using ProcLens.Application.Dumps;
using ProcLens.Application.Tests.Support;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Application.Tests;

public class DumpVaultServiceTests : IDisposable
{
    private const string Pw = "correct horse battery staple";
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-vault-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly InMemoryAuditLog _audit = new();

    public void Dispose() => Directory.Delete(_dir, true);

    private DumpVaultService Vault(IKekSource? machine = null, bool useMachine = true) =>
        new(_audit, new FakeOperator(), useMachine ? machine ?? new FixedKeySource() : null);

    private string NewPath(string name = "out.dmp") => Path.Combine(_dir, name);

    private Task<(string Path, byte[] Plain, DumpMetadata Metadata)> Encrypted(params IKekSource[] sources) =>
        Crypto.CreateEncryptedDumpAsync(_dir, sources.Length == 0 ? [new FixedKeySource()] : sources);

    // ---------- decrypt ----------

    [Fact]
    public async Task Decrypt_restores_the_original_bytes_and_is_audited()
    {
        var (path, plain, _) = await Encrypted();
        var target = NewPath();

        var result = await Vault().DecryptAsync(path, target, null);

        result.Status.Should().Be(VaultStatus.Succeeded, result.Message);
        (await File.ReadAllBytesAsync(target)).Should().Equal(plain);
        _audit.Records.Should().ContainSingle().Which.Should().Match<AuditRecord>(r =>
            r.Operation == "dump.decrypt" && r.Outcome == AuditOutcome.Succeeded && r.TargetName == "demo" && r.TargetPid == 42);
    }

    [Fact]
    public async Task Decrypt_never_overwrites_an_existing_file()
    {
        var (path, _, _) = await Encrypted();
        var target = NewPath();
        await File.WriteAllTextAsync(target, "precious");

        var result = await Vault().DecryptAsync(path, target, null);

        result.Status.Should().Be(VaultStatus.InvalidRequest);
        (await File.ReadAllTextAsync(target)).Should().Be("precious");
    }

    [Fact]
    public async Task Decrypt_with_the_wrong_key_leaves_no_output_and_is_audited_as_denied()
    {
        var (path, _, _) = await Encrypted(new FixedKeySource(1));
        var target = NewPath();

        var result = await Vault(new FixedKeySource(2)).DecryptAsync(path, target, null);

        result.Status.Should().Be(VaultStatus.CannotUnlock);
        File.Exists(target).Should().BeFalse();
        _audit.Records.Single().Outcome.Should().Be(AuditOutcome.Denied);
    }

    [Fact]
    public async Task Decrypt_of_a_modified_file_is_an_integrity_failure_and_leaves_no_plaintext()
    {
        var (path, _, _) = await Encrypted();
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^100] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        var target = NewPath();

        var result = await Vault().DecryptAsync(path, target, null);

        result.Status.Should().Be(VaultStatus.IntegrityFailure);
        File.Exists(target).Should().BeFalse("partially decrypted data must not survive a failure");
    }

    [Fact]
    public async Task Decrypt_rejects_data_that_does_not_match_the_hash_recorded_at_creation()
    {
        var (path, _, metadata) = await Encrypted();
        await File.WriteAllTextAsync(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(metadata with { PlaintextSha256 = new string('0', 64) }));
        var target = NewPath();

        var result = await Vault().DecryptAsync(path, target, null);

        result.Status.Should().Be(VaultStatus.IntegrityFailure);
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Decrypt_reports_missing_and_non_encrypted_files()
    {
        var plain = NewPath("plain.dmp");
        await File.WriteAllBytesAsync(plain, new byte[100]);

        (await Vault().DecryptAsync(NewPath("missing.enc"), NewPath(), null)).Status.Should().Be(VaultStatus.NotFound);
        (await Vault().DecryptAsync(plain, NewPath(), null)).Status.Should().Be(VaultStatus.NotEncrypted);
    }

    [Fact]
    public async Task Decrypt_with_a_password_when_there_is_no_machine_key()
    {
        using var password = SecretText.From(Pw);
        var (path, plain, _) = await Encrypted(new PasswordKekSource(password, PasswordKekSource.MinIterations));
        var target = NewPath();

        var result = await Vault(useMachine: false).DecryptAsync(path, target, password);

        result.Status.Should().Be(VaultStatus.Succeeded, result.Message);
        (await File.ReadAllBytesAsync(target)).Should().Equal(plain);
    }

    // ---------- export ----------

    [Fact]
    public async Task Export_produces_a_copy_that_only_the_password_opens_and_leaves_the_original_alone()
    {
        var (path, plain, _) = await Encrypted();
        var original = await File.ReadAllBytesAsync(path);
        var exported = NewPath("export.dmp.enc");
        using var password = SecretText.From(Pw);

        var result = await Vault().ExportAsync(path, exported, password, null);

        result.Status.Should().Be(VaultStatus.Succeeded, result.Message);
        (await File.ReadAllBytesAsync(path)).Should().Equal(original, "the source file is untouched");

        var decrypted = NewPath("from-export.dmp");
        (await Vault(useMachine: false).DecryptAsync(exported, decrypted, password)).Status.Should().Be(VaultStatus.Succeeded);
        (await File.ReadAllBytesAsync(decrypted)).Should().Equal(plain);

        (await Vault().DecryptAsync(exported, NewPath("x.dmp"), null)).Status.Should().Be(VaultStatus.CannotUnlock, "the machine key is not part of the export");
    }

    [Fact]
    public async Task Export_writes_updated_metadata_for_the_new_file()
    {
        var (path, _, metadata) = await Encrypted();
        var exported = NewPath("export.dmp.enc");
        using var password = SecretText.From(Pw);

        await Vault().ExportAsync(path, exported, password, null);

        var sidecar = DumpFiles.TryReadMetadata(exported);
        sidecar.Should().NotBeNull();
        sidecar!.DumpFilePath.Should().Be(exported);
        sidecar.Sha256.Should().Be(await DumpFiles.ComputeSha256Async(exported)).And.NotBe(metadata.Sha256);
        sidecar.PlaintextSha256.Should().Be(metadata.PlaintextSha256);
        sidecar.DumpId.Should().Be(metadata.DumpId);
    }

    [Fact]
    public async Task Export_rejects_a_weak_password_and_creates_nothing()
    {
        var (path, _, _) = await Encrypted();
        var exported = NewPath("export.dmp.enc");
        using var weak = SecretText.From("short");

        var result = await Vault().ExportAsync(path, exported, weak, null);

        result.Status.Should().Be(VaultStatus.InvalidRequest);
        File.Exists(exported).Should().BeFalse();
    }

    [Fact]
    public async Task Export_without_the_ability_to_unlock_the_source_creates_nothing()
    {
        var (path, _, _) = await Encrypted(new FixedKeySource(1));
        var exported = NewPath("export.dmp.enc");
        using var password = SecretText.From(Pw);

        var result = await Vault(new FixedKeySource(2)).ExportAsync(path, exported, password, null);

        result.Status.Should().Be(VaultStatus.CannotUnlock);
        File.Exists(exported).Should().BeFalse();
        File.Exists(DumpFiles.SidecarPath(exported)).Should().BeFalse();
    }

    // ---------- verify ----------

    [Fact]
    public async Task Verify_accepts_an_intact_encrypted_dump_without_writing_plaintext()
    {
        var (path, _, _) = await Encrypted();
        var before = Directory.GetFiles(_dir).Length;

        var result = await Vault().VerifyAsync(path, null);

        result.Status.Should().Be(VaultStatus.Succeeded);
        result.Details.Should().Contain(d => d.Contains("authenticated")).And.Contain(d => d.Contains("matches the hash recorded"));
        Directory.GetFiles(_dir).Length.Should().Be(before);
    }

    [Fact]
    public async Task Verify_detects_a_modified_file_both_by_hash_and_by_authentication()
    {
        var (path, _, _) = await Encrypted();
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x10;
        await File.WriteAllBytesAsync(path, bytes);

        var result = await Vault().VerifyAsync(path, null);

        result.Status.Should().Be(VaultStatus.IntegrityFailure);
        result.Details.Should().Contain(d => d.Contains("does NOT match"));
    }

    [Fact]
    public async Task Verify_distinguishes_a_missing_key_from_corruption()
    {
        var (path, _, _) = await Encrypted(new FixedKeySource(1));

        (await Vault(new FixedKeySource(2)).VerifyAsync(path, null)).Status.Should().Be(VaultStatus.CannotUnlock);
    }

    [Fact]
    public async Task Verify_of_a_non_dump_file_fails()
    {
        var junk = NewPath("junk.dmp");
        await File.WriteAllBytesAsync(junk, RandomNumberGenerator.GetBytes(200));

        (await Vault().VerifyAsync(junk, null)).Status.Should().Be(VaultStatus.IntegrityFailure);
    }

    // ---------- audit ----------

    [Fact]
    public async Task Every_operation_writes_exactly_one_audit_record_and_no_secrets()
    {
        using var password = SecretText.From("Tr0ub4dor&3-correct-horse");
        var (path, _, _) = await Encrypted();

        await Vault().VerifyAsync(path, null);
        await Vault().DecryptAsync(path, NewPath(), null);
        await Vault().ExportAsync(path, NewPath("e.enc"), password, null);

        _audit.Records.Select(r => r.Operation).Should().Equal("dump.verify", "dump.decrypt", "dump.export");
        _audit.AsText().Should().NotContain("Tr0ub4dor");
        AuditChain.Verify(_audit.Records.Select(AuditChain.ToLine)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_happens_when_the_audit_trail_is_unavailable()
    {
        var (path, _, _) = await Encrypted();
        _audit.Unavailable = true;
        var target = NewPath();

        var result = await Vault().DecryptAsync(path, target, null);

        result.Status.Should().Be(VaultStatus.Failed);
        File.Exists(target).Should().BeFalse("secrets are not exposed unaccountably");
    }
}
