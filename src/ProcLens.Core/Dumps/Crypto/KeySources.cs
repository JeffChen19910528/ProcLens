using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ProcLens.Core.Dumps.Crypto;

/// <param name="Descriptor">Public parameters stored in the file next to the wrapped key (e.g. KDF salt).</param>
/// <param name="Kek">The key-encryption key. Never stored.</param>
public sealed record KekMaterial(byte[] Descriptor, byte[] Kek);

/// <summary>A way of protecting (and later recovering) the per-file data key.</summary>
public interface IKekSource
{
    byte SlotType { get; }

    /// <summary>Produces fresh parameters and the key for protecting a new file.</summary>
    KekMaterial Create();

    /// <summary>Recomputes the key for an existing slot; null if this source cannot serve that descriptor.</summary>
    byte[]? Resolve(ReadOnlySpan<byte> descriptor);
}

/// <summary>Derives the key-encryption key from a password with PBKDF2-HMAC-SHA256 and a random per-file salt.</summary>
public sealed class PasswordKekSource(SecretText password, int iterations = PasswordKekSource.DefaultIterations) : IKekSource
{
    public const byte Type = 2;
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 10_000;       // refuse files that ask for a trivially cheap derivation
    public const int MaxIterations = 3_000_000;     // refuse files that would make decryption a CPU denial of service (5x today's default)
    public const int MinPasswordLength = 12;
    private const int SaltSize = 16;

    public byte SlotType => Type;

    public KekMaterial Create()
    {
        if (iterations < MinIterations) throw new InvalidOperationException("Too few key-derivation iterations.");

        var descriptor = new byte[4 + SaltSize];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, iterations);
        RandomNumberGenerator.Fill(descriptor.AsSpan(4));
        return new KekMaterial(descriptor, Derive(descriptor));
    }

    public byte[]? Resolve(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length != 4 + SaltSize) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(descriptor);
        return count is < MinIterations or > MaxIterations ? null : Derive(descriptor);
    }

    private byte[] Derive(ReadOnlySpan<byte> descriptor)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(descriptor);
        var salt = descriptor[4..].ToArray();
        var bytes = password.ToUtf8();
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, count, HashAlgorithmName.SHA256, DumpCipher.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <returns>Null if acceptable, otherwise why the password is rejected.</returns>
    public static string? CheckStrength(SecretText candidate) =>
        candidate.Length >= MinPasswordLength ? null : $"The password must be at least {MinPasswordLength} characters long.";
}
