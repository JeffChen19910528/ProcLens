using Microsoft.Win32.SafeHandles;
using ProcLens.Core.Abstractions;

namespace ProcLens.Core.Dumps;

public sealed record MinidumpInfo(
    bool IsValid,
    string? Problem,
    long FileSize,
    int StreamCount,
    DateTimeOffset? TimeStamp,
    ulong TypeFlags,
    DumpType? InferredType,
    string? Architecture,
    uint? OsMajor,
    uint? OsMinor,
    uint? OsBuild,
    int? ThreadCount,
    int? ModuleCount,
    IReadOnlyList<uint> StreamTypes);

/// <summary>
/// Reads the structure of a Windows minidump without loading it into memory. Platform-neutral and defensive:
/// the file is untrusted input, so every offset is bounds-checked and nothing is allocated from file-supplied sizes.
/// </summary>
public static class MinidumpReader
{
    private const uint Signature = 0x504D444D; // "MDMP"
    private const ushort VersionSignature = 0xA793;
    private const int HeaderSize = 32;
    private const int DirectoryEntrySize = 12;
    private const int MaxStreams = 4096;

    private const uint ThreadListStream = 3;
    private const uint ModuleListStream = 4;
    private const uint SystemInfoStream = 7;

    private const ulong FullMemoryFlag = 0x2;
    private const ulong PrivateReadWriteMemoryFlag = 0x200;

    public static MinidumpInfo Read(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = RandomAccess.GetLength(file);

        MinidumpInfo Invalid(string problem) => new(false, problem, length, 0, null, 0, null, null, null, null, null, null, null, []);

        if (length < HeaderSize) return Invalid("File is too small to be a minidump.");

        var header = ReadAt(file, 0, HeaderSize, length)!;
        if (BitConverter.ToUInt32(header, 0) != Signature) return Invalid("Missing 'MDMP' signature.");
        if ((BitConverter.ToUInt32(header, 4) & 0xFFFF) != VersionSignature) return Invalid("Unrecognised minidump version.");

        var streamCount = BitConverter.ToUInt32(header, 8);
        var directoryRva = BitConverter.ToUInt32(header, 12);
        var timestamp = BitConverter.ToUInt32(header, 20);
        var flags = BitConverter.ToUInt64(header, 24);

        if (streamCount is 0 or > MaxStreams) return Invalid($"Implausible stream count ({streamCount}).");
        var directory = ReadAt(file, directoryRva, (int)streamCount * DirectoryEntrySize, length);
        if (directory is null) return Invalid("Stream directory lies outside the file.");

        var types = new List<uint>();
        var streams = new Dictionary<uint, (uint Size, uint Rva)>();
        for (var i = 0; i < streamCount; i++)
        {
            var type = BitConverter.ToUInt32(directory, i * DirectoryEntrySize);
            var size = BitConverter.ToUInt32(directory, i * DirectoryEntrySize + 4);
            var rva = BitConverter.ToUInt32(directory, i * DirectoryEntrySize + 8);
            if ((ulong)rva + size > (ulong)length) return Invalid($"Stream {type} extends past the end of the file (truncated?).");
            types.Add(type);
            streams.TryAdd(type, (size, rva));
        }

        string? arch = null;
        uint? major = null, minor = null, build = null;
        if (streams.TryGetValue(SystemInfoStream, out var sys) && sys.Size >= 20
            && ReadAt(file, sys.Rva, 20, length) is { } si)
        {
            arch = ArchitectureName(BitConverter.ToUInt16(si, 0));
            major = BitConverter.ToUInt32(si, 8);
            minor = BitConverter.ToUInt32(si, 12);
            build = BitConverter.ToUInt32(si, 16);
        }
        else
        {
            return Invalid("No system information stream.");
        }

        return new MinidumpInfo(
            true, null, length, (int)streamCount,
            DateTimeOffset.FromUnixTimeSeconds(timestamp), flags, InferType(flags),
            arch, major, minor, build,
            ReadCount(file, streams, ThreadListStream, length),
            ReadCount(file, streams, ModuleListStream, length),
            types);
    }

    private static DumpType InferType(ulong flags) =>
        (flags & FullMemoryFlag) != 0 ? DumpType.Full
        : (flags & PrivateReadWriteMemoryFlag) != 0 ? DumpType.Heap
        : DumpType.Mini;

    private static int? ReadCount(SafeFileHandle file, Dictionary<uint, (uint Size, uint Rva)> streams, uint type, long length) =>
        streams.TryGetValue(type, out var s) && s.Size >= 4 && ReadAt(file, s.Rva, 4, length) is { } b
            ? (int)Math.Min(BitConverter.ToUInt32(b, 0), int.MaxValue)
            : null;

    private static string ArchitectureName(ushort arch) => arch switch
    {
        0 => "x86",
        5 => "ARM",
        6 => "IA64",
        9 => "x64",
        12 => "ARM64",
        _ => $"unknown ({arch})",
    };

    private static byte[]? ReadAt(SafeFileHandle file, long offset, int count, long fileLength)
    {
        if (offset < 0 || count < 0 || offset + count > fileLength) return null;
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = RandomAccess.Read(file, buffer.AsSpan(read), offset + read);
            if (n == 0) return null;
            read += n;
        }
        return buffer;
    }
}
