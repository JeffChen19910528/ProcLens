using System.Runtime.Versioning;
using System.Security.Cryptography;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;

namespace ProcLens.Platform.Windows;

/// <summary>
/// The per-user master key. 32 random bytes are generated once and stored protected with DPAPI (CurrentUser scope),
/// so only the same Windows user on the same machine can recover them. Each dump gets its own random data key,
/// which is wrapped by this master key; the master key never encrypts dump contents directly.
/// Losing the key (new profile, reinstalled Windows) makes dumps protected only by it unreadable, which is why
/// a password-protected export exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMachineKeySource(string? keyDirectory = null) : IKekSource
{
    public const byte Type = 1;
    private static readonly byte[] Entropy = "ProcLens master key v1"u8.ToArray();
    private const int KeyIdSize = 8;

    private readonly string _directory = keyDirectory ?? ProcLensPaths.KeyDirectory;
    private byte[]? _cached;

    public byte SlotType => Type;

    private string KeyFile => Path.Combine(_directory, "master.key");

    public KekMaterial Create()
    {
        var key = LoadExisting() ?? CreateKey();
        return new KekMaterial(KeyId(key), (byte[])key.Clone());
    }

    public byte[]? Resolve(ReadOnlySpan<byte> descriptor)
    {
        byte[]? key;
        try { key = LoadExisting(); }
        catch (CryptographicException) { return null; } // protected by another user or machine

        return key is not null && descriptor.SequenceEqual(KeyId(key)) ? (byte[])key.Clone() : null;
    }

    private static byte[] KeyId(byte[] key) => SHA256.HashData(key)[..KeyIdSize];

    /// <exception cref="CryptographicException">The file exists but cannot be unprotected by this user.</exception>
    private byte[]? LoadExisting()
    {
        if (_cached is not null) return _cached;
        if (!File.Exists(KeyFile)) return null;

        var blob = File.ReadAllBytes(KeyFile);
        return _cached = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
    }

    private byte[] CreateKey()
    {
        RestrictedFile.CreateDirectory(_directory);
        var key = RandomNumberGenerator.GetBytes(DumpCipher.KeySize);
        var blob = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var stream = RestrictedFile.CreateNew(KeyFile);
            stream.Write(blob);
        }
        catch (IOException) when (File.Exists(KeyFile))
        {
            // Another ProcLens process created it first; use theirs so both agree.
            CryptographicOperations.ZeroMemory(key);
            return LoadExisting() ?? throw new InvalidOperationException("The master key file disappeared.");
        }
        return _cached = key;
    }
}
