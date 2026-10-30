using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProcLens.Core.Abstractions;
using ProcLens.Platform.Windows.Native;

namespace ProcLens.Platform.Windows;

/// <param name="Success">True if the native call reported success.</param>
/// <param name="Error">
/// On failure, the value from GetLastError. DbgHelp usually reports an HRESULT here (e.g. 0x8007012B for
/// ERROR_PARTIAL_COPY), not a plain Win32 code.
/// </param>
public readonly record struct NativeDumpOutcome(bool Success, int Error);

/// <summary>
/// The only seam to the native dump API. Production code uses <see cref="NativeMiniDumpWriter"/>; tests substitute
/// fakes to exercise failure, cancellation and concurrency without needing a process that misbehaves on demand.
/// </summary>
public interface IMiniDumpWriter
{
    NativeDumpOutcome Write(SafeProcessHandle process, int processId, SafeFileHandle file, DumpType type, CancellationToken ct);
}

public static class MiniDumpFlags
{
    private const uint WithDataSegs = 0x1;
    private const uint WithFullMemory = 0x2;
    private const uint WithUnloadedModules = 0x20;
    private const uint WithProcessThreadData = 0x100;
    private const uint WithPrivateReadWriteMemory = 0x200;
    private const uint WithFullMemoryInfo = 0x800;
    private const uint WithThreadInfo = 0x1000;

    // MiniDumpWithHandleData is deliberately not used: it needs additional process rights and records
    // the names of kernel objects the target has open.
    public static uint For(DumpType type) => type switch
    {
        DumpType.Mini => WithThreadInfo | WithUnloadedModules | WithProcessThreadData,
        DumpType.Heap => WithThreadInfo | WithUnloadedModules | WithProcessThreadData
                         | WithPrivateReadWriteMemory | WithDataSegs | WithFullMemoryInfo,
        DumpType.Full => WithThreadInfo | WithUnloadedModules | WithProcessThreadData
                         | WithFullMemory | WithFullMemoryInfo,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown dump type."),
    };
}

public sealed class NativeMiniDumpWriter : IMiniDumpWriter
{
    private const int CancelCallbackType = 6;

    public NativeDumpOutcome Write(SafeProcessHandle process, int processId, SafeFileHandle file, DumpType type, CancellationToken ct)
    {
        // CALLBACK_INPUT is packed to 4: { ULONG ProcessId; HANDLE ProcessHandle; ULONG CallbackType; ... }
        var typeOffset = IntPtr.Size == 8 ? 12 : 8;

        var callbackFailed = false;
        MiniDumpCallbackRoutine routine = (_, input, output) =>
        {
            // An exception escaping a native callback would take the whole process down. Treat any surprise as a
            // reason to abort this dump instead (returning false makes DbgHelp stop and report failure).
            try
            {
                if (Marshal.ReadInt32(input, typeOffset) == CancelCallbackType)
                {
                    // CALLBACK_OUTPUT for CancelCallback: { BOOL CheckCancel; BOOL Cancel; }
                    Marshal.WriteInt32(output, 0, 1);
                    Marshal.WriteInt32(output, 4, ct.IsCancellationRequested ? 1 : 0);
                }
                return true;
            }
            catch (Exception)
            {
                callbackFailed = true;
                return false;
            }
        };

        var callback = new MiniDumpCallbackInformation
        {
            CallbackRoutine = Marshal.GetFunctionPointerForDelegate(routine),
            CallbackParam = IntPtr.Zero,
        };

        var ok = DbgHelp.MiniDumpWriteDump(
            process, (uint)processId, file, MiniDumpFlags.For(type), IntPtr.Zero, IntPtr.Zero, ref callback);
        var error = ok ? 0 : Marshal.GetLastWin32Error();

        GC.KeepAlive(routine); // the native side holds only a raw function pointer
        const int EAbort = unchecked((int)0x80004004);
        return callbackFailed ? new NativeDumpOutcome(false, EAbort) : new NativeDumpOutcome(ok, error);
    }
}
