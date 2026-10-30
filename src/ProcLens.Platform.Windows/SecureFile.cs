using System.IO.MemoryMappedFiles;
using ProcLens.Core.Security;
using ProcLens.Platform.Windows.Native;

namespace ProcLens.Platform.Windows;

internal static class SecureFile
{
    /// <summary>Creates a new file only the current user can access; never overwrites; not shared while open.</summary>
    public static FileStream CreateNew(string path) => RestrictedFile.CreateNew(path);
}

internal static class DumpVerifier
{
    private const uint ThreadListStream = 3;
    private const uint SystemInfoStream = 7;

    /// <summary>
    /// Asks DbgHelp itself to locate the core streams, i.e. the same library debuggers build on must be able to
    /// open the file. Returns a description of the problem, or null if it passed (or the file could not be mapped,
    /// which is not evidence of corruption).
    /// </summary>
    public static string? VerifyWithDbgHelp(string path)
    {
        MemoryMappedFile mapping;
        MemoryMappedViewAccessor view;
        try
        {
            mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException or UnauthorizedAccessException)
        {
            return null;
        }

        using (mapping)
        using (view)
        {
            var handle = view.SafeMemoryMappedViewHandle;
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var basePointer = handle.DangerousGetHandle();
                foreach (var stream in new[] { SystemInfoStream, ThreadListStream })
                {
                    if (!DbgHelp.MiniDumpReadDumpStream(basePointer, stream, out _, out _, out _))
                        return $"DbgHelp could not read stream {stream}.";
                }
                return null;
            }
            finally
            {
                if (added) handle.DangerousRelease();
            }
        }
    }
}
