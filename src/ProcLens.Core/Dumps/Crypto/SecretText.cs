using System.Security.Cryptography;
using System.Text;

namespace ProcLens.Core.Dumps.Crypto;

/// <summary>
/// Holds a password so that it cannot leak through <c>ToString()</c>, record printing, logging or JSON
/// serialization: there is no public member that exposes the text. The buffer is wiped on dispose.
/// </summary>
public sealed class SecretText : IDisposable
{
    private char[] _chars;

    private SecretText(char[] chars) => _chars = chars;

    public static SecretText From(string value) => new(value.ToCharArray());

    public int Length => _chars.Length;

    /// <summary>The caller must zero the returned array when done (see <see cref="CryptographicOperations.ZeroMemory"/>).</summary>
    public byte[] ToUtf8() => Encoding.UTF8.GetBytes(_chars);

    public override string ToString() => "[redacted]";

    public void Dispose()
    {
        Array.Clear(_chars);
        _chars = [];
    }
}
