namespace ProcLens.Core.Tests;

/// <summary>Builds a minimal but structurally valid minidump (header, directory, SystemInfo and ThreadList streams).</summary>
internal static class TestMinidump
{
    public static byte[] Build(ulong flags = 0x1000, ushort arch = 9, uint threads = 25, Action<BinaryWriter>? tamper = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        const int streamCount = 2;
        var directoryRva = 32u;
        var sysRva = directoryRva + streamCount * 12;
        var threadRva = sysRva + 56;

        w.Write(0x504D444Du);              // "MDMP"
        w.Write(0xA793u);                  // version
        w.Write((uint)streamCount);
        w.Write(directoryRva);
        w.Write(0u);                       // checksum
        w.Write(1_700_000_000u);           // timestamp
        w.Write(flags);

        w.Write(7u); w.Write(56u); w.Write(sysRva);       // SystemInfoStream
        w.Write(3u); w.Write(4u); w.Write(threadRva);     // ThreadListStream

        w.Write(arch); w.Write((ushort)6); w.Write((ushort)0); w.Write((byte)8); w.Write((byte)1);
        w.Write(10u); w.Write(0u); w.Write(26300u);       // major, minor, build
        w.Write(new byte[56 - 20]);
        w.Write(threads);

        tamper?.Invoke(w);
        return ms.ToArray();
    }

    /// <summary>The same dump padded with pseudo-random data, so it spans several cipher chunks.</summary>
    public static byte[] BuildLarge(int extraBytes)
    {
        var extra = new byte[extraBytes];
        new Random(42).NextBytes(extra);
        return [.. Build(), .. extra];
    }
}
