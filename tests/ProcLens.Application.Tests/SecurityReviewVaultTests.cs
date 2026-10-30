using FluentAssertions;
using ProcLens.Application.Dumps;
using ProcLens.Application.Tests.Support;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Application.Tests;

/// <summary>Regression tests for the security review findings that concern the vault.</summary>
public class SecurityReviewVaultTests : IDisposable
{
    private const string Pw = "correct horse battery staple";
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-srv-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly InMemoryAuditLog _audit = new();

    public void Dispose() => Directory.Delete(_dir, true);

    private DumpVaultService Vault() => new(_audit, new FakeOperator(), new FixedKeySource());

    [Fact]
    public async Task Export_refuses_to_copy_a_modified_dump_and_creates_nothing()
    {
        var (path, _, _) = await Crypto.CreateEncryptedDumpAsync(_dir, [new FixedKeySource()]);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x01;                     // damage in the middle of the ciphertext
        await File.WriteAllBytesAsync(path, bytes);
        var output = Path.Combine(_dir, "export.dmp.enc");
        using var password = SecretText.From(Pw);

        var result = await Vault().ExportAsync(path, output, password, null);

        result.Status.Should().Be(VaultStatus.IntegrityFailure);
        File.Exists(output).Should().BeFalse("a damaged dump must not be laundered into a fresh-looking export");
        File.Exists(DumpFiles.SidecarPath(output)).Should().BeFalse();
    }

    [Fact]
    public async Task Export_refuses_a_dump_whose_content_no_longer_matches_the_recorded_original_hash()
    {
        var (path, _, metadata) = await Crypto.CreateEncryptedDumpAsync(_dir, [new FixedKeySource()]);
        await File.WriteAllTextAsync(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(metadata with { PlaintextSha256 = new string('0', 64) }));
        var output = Path.Combine(_dir, "export.dmp.enc");
        using var password = SecretText.From(Pw);

        var result = await Vault().ExportAsync(path, output, password, null);

        result.Status.Should().Be(VaultStatus.IntegrityFailure);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task An_intact_dump_still_exports()
    {
        var (path, plain, _) = await Crypto.CreateEncryptedDumpAsync(_dir, [new FixedKeySource()]);
        var output = Path.Combine(_dir, "export.dmp.enc");
        using var password = SecretText.From(Pw);

        (await Vault().ExportAsync(path, output, password, null)).Status.Should().Be(VaultStatus.Succeeded);

        var restored = Path.Combine(_dir, "restored.dmp");
        await new DumpVaultService(_audit, new FakeOperator(), null).DecryptAsync(output, restored, password);
        (await File.ReadAllBytesAsync(restored)).Should().Equal(plain);
    }

    [Theory]
    [InlineData("host:stream.dmp")]   // not "a:b": a single letter plus colon is a drive, so that would be a different (drive-relative) path
    [InlineData("secret.dmp:hidden")]
    [InlineData("NUL")]
    [InlineData("out\u0001.dmp")]
    public async Task Output_paths_that_would_hide_the_data_or_go_to_a_device_are_refused(string name)
    {
        if (!OperatingSystem.IsWindows() && name != "out\u0001.dmp") return; // colons and device names are ordinary on Unix
        var (path, _, _) = await Crypto.CreateEncryptedDumpAsync(_dir, [new FixedKeySource()]);
        using var password = SecretText.From(Pw);

        var decrypt = await Vault().DecryptAsync(path, Path.Combine(_dir, name), null);
        var export = await Vault().ExportAsync(path, Path.Combine(_dir, name), password, null);

        decrypt.Status.Should().Be(VaultStatus.InvalidRequest);
        export.Status.Should().Be(VaultStatus.InvalidRequest);
        Directory.GetFiles(_dir).Should().NotContain(f => f.Contains("hidden") || Path.GetFileName(f) == "a");
    }
}
