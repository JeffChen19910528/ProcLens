using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcLens.Platform.Windows.Native;

/// <summary>MINIDUMP_CALLBACK_INFORMATION (packed to 4 bytes by dbghelp.h).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MiniDumpCallbackInformation
{
    public IntPtr CallbackRoutine;
    public IntPtr CallbackParam;
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
[return: MarshalAs(UnmanagedType.Bool)]
internal delegate bool MiniDumpCallbackRoutine(IntPtr callbackParam, IntPtr callbackInput, IntPtr callbackOutput);

internal static class DbgHelp
{
    // DbgHelp is loaded from System32 only, so a planted dbghelp.dll next to the executable or in the
    // working directory is never picked up.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MiniDumpWriteDump(
        SafeProcessHandle process,
        uint processId,
        SafeFileHandle file,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        ref MiniDumpCallbackInformation callbackParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MiniDumpReadDumpStream(
        IntPtr baseOfDump,
        uint streamNumber,
        out IntPtr directory,
        out IntPtr streamPointer,
        out uint streamSize);
}

internal static class ProcessTimes
{
    public const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(
        SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
