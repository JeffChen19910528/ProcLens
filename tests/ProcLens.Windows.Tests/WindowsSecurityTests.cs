using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;
using ProcLens.Platform.Windows;

namespace ProcLens.Windows.Tests;

internal sealed class FixedKeySource(byte seed = 1) : IKekSource
{
    private readonly byte[] _key = Enumerable.Repeat(seed, 32).ToArray();
    public byte SlotType => 1;
    public KekMaterial Create() => new([0xAA], (byte[])_key.Clone());
    public byte[]? Resolve(ReadOnlySpan<byte> descriptor) => descriptor.SequenceEqual(new byte[] { 0xAA }) ? (byte[])_key.Clone() : null;
}

internal sealed class HookedKeySource(Action onCreate) : IKekSource
{
    public byte SlotType => 1;
    public KekMaterial Create() { onCreate(); return new([0xAA], new byte[32]); }
    public byte[]? Resolve(ReadOnlySpan<byte> descriptor) => null;
}

internal sealed class CapturingAuthorizer : IDumpAuthorizer
{
    public DumpAuthorizationContext? Seen { get; private set; }

    public AuthorizationDecision Authorize(DumpAuthorizationContext context)
    {
        Seen = context;
        return new AuthorizationDecision(true, "captured", "owner");
    }
}

[SupportedOSPlatform("windows")]
public class WindowsEncryptedDumpTests : IDisposable
{
    private const string Pw = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proclens-enc-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public WindowsEncryptedDumpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private TargetProcess Target()
    {
        var t = new TargetProcess();
        _disposables.Add(t);
        return t;
    }

    private string[] Files() => Directory.GetFileSystemEntries(_dir);

    private static async Task<byte[]> DecryptAsync(string path, params IKekSource[] sources)
    {
        await using var input = File.OpenRead(path);
        using var output = new MemoryStream();
        await DumpCipher.DecryptAsync(input, output, sources);
        return output.ToArray();
    }

    // ---------- encryption at rest ----------

    [SkippableOnNonWindows]
    public async Task Encrypted_dump_is_stored_only_as_ciphertext_and_decrypts_to_a_valid_minidump()
    {
        var target = Target();
        var key = new FixedKeySource();

        var result = await new WindowsProcessDumpProvider(machineKey: key).CreateDumpAsync(target.Request(_dir, DumpType.Heap) with { Encrypt = true });

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        result.FilePath.Should().EndWith(".dmp.enc");
        Files().Should().HaveCount(2, "only the encrypted dump and its metadata: no temporary plaintext may remain")
            .And.NotContain(f => f.EndsWith(".tmp") || f.EndsWith(".dmp"));
        DumpCipher.IsEncrypted(result.FilePath!).Should().BeTrue();

        var plain = await DecryptAsync(result.FilePath!, key);
        var path = Path.Combine(_dir, "roundtrip.dmp");
        await File.WriteAllBytesAsync(path, plain);
        MinidumpReader.Read(path).Should().Match<MinidumpInfo>(i => i.IsValid && i.InferredType == DumpType.Heap);
        Convert.ToHexStringLower(SHA256.HashData(plain)).Should().Be(result.Metadata!.PlaintextSha256);
    }

    [SkippableOnNonWindows]
    public async Task Metadata_records_both_hashes_the_scheme_and_the_authorization()
    {
        var target = Target();

        var result = await new WindowsProcessDumpProvider(machineKey: new FixedKeySource()).CreateDumpAsync(target.Request(_dir) with { Encrypt = true });

        var meta = result.Metadata!;
        meta.EncryptionScheme.Should().Be(DumpCipher.SchemeName);
        meta.Sha256.Should().Be(await DumpFiles.ComputeSha256Async(result.FilePath!), "Sha256 describes the stored (encrypted) file");
        meta.PlaintextSha256.Should().NotBe(meta.Sha256);
        meta.FileSize.Should().Be(new FileInfo(result.FilePath!).Length);
        DumpFiles.TryReadMetadata(result.FilePath!).Should().Be(meta);
        result.Authorization.Should().Match<AuthorizationDecision>(a => a.Allowed && a.Policy == "owner");
    }

    [SkippableOnNonWindows]
    public async Task The_ciphertext_does_not_contain_recognisable_process_memory()
    {
        var target = Target();
        var key = new FixedKeySource();
        var provider = new WindowsProcessDumpProvider(machineKey: key);
        var plainResult = await provider.CreateDumpAsync(target.Request(_dir, DumpType.Full));
        var encResult = await provider.CreateDumpAsync(target.Request(_dir, DumpType.Full) with { Encrypt = true });

        var plain = await File.ReadAllBytesAsync(plainResult.FilePath!);
        var cipher = await File.ReadAllBytesAsync(encResult.FilePath!);

        // The plaintext dump of any Windows process contains the module name in UTF-16; the ciphertext must not.
        var needle = System.Text.Encoding.Unicode.GetBytes("kernel32.dll".ToUpperInvariant());
        var needleLower = System.Text.Encoding.Unicode.GetBytes("kernel32.dll");
        (plain.AsSpan().IndexOf(needle) >= 0 || plain.AsSpan().IndexOf(needleLower) >= 0).Should().BeTrue("sanity: the signature is visible in the plain dump");
        cipher.AsSpan().IndexOf(needle).Should().Be(-1);
        cipher.AsSpan().IndexOf(needleLower).Should().Be(-1);
    }

    [SkippableOnNonWindows]
    public async Task A_password_alone_protects_a_dump_without_any_machine_key()
    {
        var target = Target();
        using var password = SecretText.From(Pw);

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir) with { Password = password });

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        var plain = await DecryptAsync(result.FilePath!, new PasswordKekSource(password));
        plain.Length.Should().BeGreaterThan(0);
        await using var header = File.OpenRead(result.FilePath!);
        var info = await DumpCipher.ReadInfoAsync(header);
        info.SlotTypes.Should().Equal(PasswordKekSource.Type);
    }

    [SkippableOnNonWindows]
    public async Task Machine_key_and_password_can_both_open_the_same_file()
    {
        var target = Target();
        var key = new FixedKeySource();
        using var password = SecretText.From(Pw);

        var result = await new WindowsProcessDumpProvider(machineKey: key)
            .CreateDumpAsync(target.Request(_dir) with { Encrypt = true, Password = password });

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        var viaKey = await DecryptAsync(result.FilePath!, key);
        var viaPassword = await DecryptAsync(result.FilePath!, new PasswordKekSource(password));
        viaPassword.Should().Equal(viaKey);
    }

    [SkippableOnNonWindows]
    public async Task Weak_passwords_and_missing_machine_keys_are_rejected_before_anything_is_written()
    {
        var target = Target();
        using var weak = SecretText.From("short");

        (await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir) with { Password = weak })).Status.Should().Be(DumpStatus.InvalidRequest);
        var noKey = await new WindowsProcessDumpProvider(machineKey: null).CreateDumpAsync(target.Request(_dir) with { Encrypt = true });

        noKey.Status.Should().Be(DumpStatus.InvalidRequest);
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task A_key_failure_during_encryption_leaves_neither_plaintext_nor_ciphertext()
    {
        var target = Target();
        var broken = new HookedKeySource(() => throw new CryptographicException("master key unavailable"));

        var result = await new WindowsProcessDumpProvider(machineKey: broken).CreateDumpAsync(target.Request(_dir) with { Encrypt = true });

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("master key unavailable");
        Files().Should().BeEmpty("the temporary plaintext dump must be wiped when encryption fails");
    }

    [SkippableOnNonWindows]
    public async Task Cancelling_during_encryption_removes_the_plaintext_and_the_partial_ciphertext()
    {
        var target = Target();
        using var cts = new CancellationTokenSource();
        var cancelling = new HookedKeySource(cts.Cancel);

        var result = await new WindowsProcessDumpProvider(machineKey: cancelling)
            .CreateDumpAsync(target.Request(_dir) with { Encrypt = true }, cts.Token);

        result.Status.Should().Be(DumpStatus.Cancelled);
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Plaintext_dumps_remain_available_when_encryption_is_not_requested()
    {
        var target = Target();

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir));

        result.FilePath.Should().EndWith(".dmp");
        result.Metadata!.EncryptionScheme.Should().BeNull();
        result.Metadata.Sha256.Should().Be(result.Metadata.PlaintextSha256);
    }

    // ---------- administrative authorization plumbing ----------

    [SkippableOnNonWindows]
    public async Task The_administrative_authorization_reaches_the_authorizer_and_the_decision_comes_back()
    {
        var target = Target();
        var authorizer = new CapturingAuthorizer();
        var admin = new AdminAuthorization("incident 4711");

        var result = await new WindowsProcessDumpProvider(authorizer: authorizer).CreateDumpAsync(target.Request(_dir) with { Admin = admin });

        authorizer.Seen!.Admin.Should().Be(admin);
        authorizer.Seen.OperatorIdentity.Should().Be(WindowsIdentity.GetCurrent().Name);
        authorizer.Seen.TargetOwner.Should().Be(WindowsIdentity.GetCurrent().Name);
        result.Authorization.Should().Be(new AuthorizationDecision(true, "captured", "owner"));
    }

    [SkippableOnNonWindows]
    public async Task A_denial_carries_its_decision_back_for_auditing()
    {
        var target = Target();

        var result = await new WindowsProcessDumpProvider(authorizer: new DenyAll()).CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Unauthorized);
        result.Authorization!.Allowed.Should().BeFalse();
    }

    // ---------- output directory hardening ----------

    [SkippableOnNonWindows]
    public async Task A_directory_created_by_ProcLens_is_private_to_the_current_user()
    {
        var target = Target();
        var fresh = Path.Combine(_dir, "private");

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(fresh));

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        var rules = new DirectoryInfo(fresh).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.Should().ContainSingle().Which.IdentityReference.Should().Be(WindowsIdentity.GetCurrent().User);
        rules[0].IsInherited.Should().BeFalse();
        result.Warnings.Should().BeNullOrEmpty();
    }

    [SkippableOnNonWindows]
    public async Task An_existing_directory_that_other_users_can_list_produces_a_warning_but_still_works()
    {
        var target = Target();
        var shared = Directory.CreateDirectory(Path.Combine(_dir, "shared"));
        var acl = shared.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        shared.SetAccessControl(acl);

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(shared.FullName));

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        result.Warnings.Should().ContainSingle(w => w.Contains("Users") && w.Contains("can be listed"));
        var rules = new FileInfo(result.FilePath!).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        rules.Count.Should().Be(1, "the dump file itself stays private even in a shared folder");
    }
}

[SupportedOSPlatform("windows")]
public class DpapiMachineKeySourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proclens-key-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [SkippableOnNonWindows]
    public void The_key_persists_and_the_same_key_is_found_again_by_a_new_instance()
    {
        var created = new DpapiMachineKeySource(_dir).Create();

        var again = new DpapiMachineKeySource(_dir).Resolve(created.Descriptor);

        again.Should().Equal(created.Kek);
        created.Kek.Should().HaveCount(DumpCipher.KeySize);
    }

    [SkippableOnNonWindows]
    public void The_key_file_does_not_contain_the_raw_key_and_is_private_to_the_user()
    {
        var material = new DpapiMachineKeySource(_dir).Create();
        var file = Path.Combine(_dir, "master.key");

        File.ReadAllBytes(file).AsSpan().IndexOf(material.Kek).Should().Be(-1, "it is stored DPAPI-protected, not in the clear");
        var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.Should().ContainSingle().Which.IdentityReference.Should().Be(WindowsIdentity.GetCurrent().User);
    }

    [SkippableOnNonWindows]
    public void Resolving_never_creates_a_key_and_unknown_key_ids_are_not_served()
    {
        var source = new DpapiMachineKeySource(_dir);

        source.Resolve(new byte[8]).Should().BeNull();
        Directory.Exists(_dir).Should().BeFalse("decrypting must not silently generate a fresh (useless) key");

        source.Create();
        source.Resolve(new byte[8]).Should().BeNull("a different key id belongs to another key");
        source.Resolve([]).Should().BeNull();
    }

    [SkippableOnNonWindows]
    public void Different_key_directories_hold_independent_keys()
    {
        var a = new DpapiMachineKeySource(Path.Combine(_dir, "a")).Create();
        var b = new DpapiMachineKeySource(Path.Combine(_dir, "b")).Create();

        a.Kek.Should().NotEqual(b.Kek);
    }

    [SkippableOnNonWindows]
    public void A_corrupted_key_file_is_reported_not_silently_replaced()
    {
        new DpapiMachineKeySource(_dir).Create();
        File.WriteAllBytes(Path.Combine(_dir, "master.key"), new byte[40]);
        var fresh = new DpapiMachineKeySource(_dir);

        fresh.Resolve(new byte[8]).Should().BeNull();
        var act = () => fresh.Create();

        act.Should().Throw<CryptographicException>("replacing the key would orphan every dump protected by it");
    }

    [SkippableOnNonWindows]
    public async Task A_dump_encrypted_with_the_machine_key_can_only_be_opened_with_the_same_key_directory()
    {
        var plain = RandomNumberGenerator.GetBytes(10_000);
        var first = new DpapiMachineKeySource(Path.Combine(_dir, "one"));
        using var cipher = new MemoryStream();
        await DumpCipher.EncryptAsync(new MemoryStream(plain), cipher, [first]);

        using var ok = new MemoryStream();
        await DumpCipher.DecryptAsync(new MemoryStream(cipher.ToArray()), ok, [new DpapiMachineKeySource(Path.Combine(_dir, "one"))]);
        ok.ToArray().Should().Equal(plain);

        var other = new DpapiMachineKeySource(Path.Combine(_dir, "two"));
        other.Create();
        var act = () => DumpCipher.DecryptAsync(new MemoryStream(cipher.ToArray()), new MemoryStream(), [other]);
        (await act.Should().ThrowAsync<DumpDecryptionException>()).Which.Kind.Should().Be(DecryptionFailure.KeyUnavailable);
    }
}

[SupportedOSPlatform("windows")]
public class RestrictedFileWindowsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-rfw-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [SkippableOnNonWindows]
    public void Audit_style_files_are_private_to_the_current_user()
    {
        var path = Path.Combine(_dir, "audit.jsonl");
        using (RestrictedFile.OpenOrCreate(path, FileShare.None)) { }

        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        rules.Should().ContainSingle().Which.IdentityReference.Should().Be(WindowsIdentity.GetCurrent().User);
    }

    [SkippableOnNonWindows]
    public void Reopening_an_existing_restricted_file_keeps_its_content_and_does_not_change_its_acl()
    {
        var path = Path.Combine(_dir, "keep.bin");
        using (var first = RestrictedFile.OpenOrCreate(path, FileShare.None)) first.Write([1, 2, 3]);

        using (var second = RestrictedFile.OpenOrCreate(path, FileShare.None))
            second.Length.Should().Be(3);

        new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Count.Should().Be(1);
    }

    [SkippableOnNonWindows]
    public void Broad_readers_are_listed_for_a_shared_directory_and_empty_for_a_private_one()
    {
        var shared = Directory.CreateDirectory(Path.Combine(_dir, "shared"));
        var acl = shared.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ListDirectory, AccessControlType.Allow));
        shared.SetAccessControl(acl);
        var priv = Path.Combine(_dir, "private");
        RestrictedFile.CreateDirectory(priv);

        RestrictedFile.BroadReaders(shared.FullName).Should().NotBeEmpty();
        RestrictedFile.BroadReaders(priv).Should().BeEmpty();
        RestrictedFile.BroadReaders(Path.Combine(_dir, "missing")).Should().BeEmpty();
    }
}
