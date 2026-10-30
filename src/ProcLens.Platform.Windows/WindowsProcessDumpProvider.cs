using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Platform;
using ProcLens.Platform.Windows.Native;

namespace ProcLens.Platform.Windows;

/// <summary>
/// The Windows-specific half of making a dump: who may be dumped, opening the process with the least rights,
/// proving it is the process the caller meant, serialising DbgHelp, and calling it. What happens to the file
/// afterwards (verification, hashing, encryption, metadata, clean-up) is <see cref="IDumpFinalizer"/>'s job.
/// </summary>
public sealed class WindowsProcessDumpProvider : IProcessDumpProvider
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPartialCopy = 299;
    private const int ErrorDiskFull = 112;
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// The access MiniDumpWriteDump documents as necessary. Every dump type needs exactly this, and nothing
    /// broader (no PROCESS_ALL_ACCESS, no PROCESS_DUP_HANDLE) is ever requested.
    /// </summary>
    public const uint RequiredAccess = ProcessQueryInformation | ProcessVmRead;

    /// <summary>DbgHelp is not thread-safe, so every call into it from this library goes through this one gate.</summary>
    private static readonly SemaphoreSlim DbgHelpGate = new(1, 1);

    private readonly IMiniDumpWriter _writer;
    private readonly IDumpAuthorizer _authorizer;
    private readonly TimeProvider _time;
    private readonly IKekSource? _machineKey;
    private readonly IDumpFinalizer _finalizer;

    public WindowsProcessDumpProvider(
        IMiniDumpWriter? writer = null, IDumpAuthorizer? authorizer = null, TimeProvider? time = null,
        IKekSource? machineKey = null, IDumpFinalizer? finalizer = null)
    {
        _writer = writer ?? new NativeMiniDumpWriter();
        _authorizer = authorizer ?? new SameUserDumpAuthorizer();
        _time = time ?? TimeProvider.System;
        _machineKey = machineKey;
        _finalizer = finalizer ?? new DumpFinalizer(_time);
    }

    public PlatformKind Platform => PlatformKind.Windows;

    public bool IsSupported => true;

    public async Task<DumpResult> CreateDumpAsync(DumpRequest request, CancellationToken ct = default)
    {
        // 1. Validate the request (type, identity inputs, encryption options, output directory).
        if (request.ProcessId <= 0 || !Enum.IsDefined(request.Type))
            return Fail(DumpStatus.InvalidRequest, "Invalid process id or dump type.");
        if (request.ProcessId == Environment.ProcessId)
            return Fail(DumpStatus.InvalidRequest, "ProcLens cannot dump itself; dump it from another process.");
        if (request.ExpectedStartTime is not { } expectedStart)
            return Fail(DumpStatus.InvalidRequest, "The process start time is required to guard against PID reuse.");
        if (request.Encrypt && _machineKey is null)
            return Fail(DumpStatus.InvalidRequest, "Encryption with the machine key is not available in this configuration.");
        if (request.Password is { } password && PasswordKekSource.CheckStrength(password) is { } weak)
            return Fail(DumpStatus.InvalidRequest, weak);

        var directory = DumpOutputDirectory.Prepare(request.OutputDirectory);
        if (!directory.IsUsable)
            return Fail(DumpStatus.InvalidRequest, directory.Problem!);

        // 2. The target must exist.
        string processName;
        try
        {
            using var probe = Process.GetProcessById(request.ProcessId);
            processName = request.ProcessName ?? probe.ProcessName;
        }
        catch (ArgumentException)
        {
            return Fail(DumpStatus.ProcessNotFound, $"Process {request.ProcessId} was not found.", warnings: directory.Warnings);
        }

        // 3. Authorization, independent of what the OS would allow.
        var decision = _authorizer.Authorize(new DumpAuthorizationContext(
            request.ProcessId, WindowsProcessProvider.QueryOwner(request.ProcessId), WindowsIdentity.GetCurrent().Name, request.Admin));
        if (!decision.Allowed)
            return Fail(DumpStatus.Unauthorized, decision.Reason, decision, directory.Warnings);

        // 4. Open the process with the minimum rights. Holding the handle also pins the process object, so the
        //    PID cannot be recycled for a different process while we work.
        using var process = NativeMethods.OpenProcess(RequiredAccess, false, (uint)request.ProcessId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error switch
            {
                ErrorInvalidParameter => Fail(DumpStatus.ProcessNotFound, $"Process {request.ProcessId} was not found.", decision, directory.Warnings),
                ErrorAccessDenied => Fail(DumpStatus.AccessDenied,
                    "Windows denied access to the process. ProcLens does not elevate privileges or bypass process protection.", decision, directory.Warnings),
                _ => Fail(DumpStatus.Failed, $"OpenProcess failed: {new Win32Exception(error).Message} (Win32 error {error}).", decision, directory.Warnings),
            };
        }

        // 5. Serialize every DbgHelp operation.
        try
        {
            await DbgHelpGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return Fail(DumpStatus.Cancelled, "Cancelled while waiting for another dump to finish.", decision, directory.Warnings);
        }

        try
        {
            return await DumpWhileHoldingGateAsync(request, process, processName, expectedStart, directory.Path!, decision, directory.Warnings, ct);
        }
        finally
        {
            DbgHelpGate.Release();
        }
    }

    private async Task<DumpResult> DumpWhileHoldingGateAsync(
        DumpRequest request, SafeProcessHandle process, string processName, DateTimeOffset expectedStart, string directory,
        AuthorizationDecision decision, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        // Identity is re-checked here, after any wait, using the handle (not the PID).
        if (VerifyIdentity(process, expectedStart) is { } identityProblem)
            return Fail(DumpStatus.ProcessNotFound, identityProblem, decision, warnings);

        var protectedDump = request.Encrypt || request.Password is not null;
        var stem = DumpFileNames.Stem(processName, request.ProcessId, request.Type, _time.GetUtcNow());
        var rawPath = DumpFileNames.RawPath(directory, stem, protectedDump);
        var finalPath = DumpFileNames.FinalPath(directory, stem, protectedDump);

        DumpResult Failure(DumpStatus status, string message) => Fail(status, message, decision, warnings);

        FileStream? stream = null;
        var handedOver = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            stream = SecureFile.CreateNew(rawPath);

            var outcome = await WriteNativeAsync(request, process, stream, ct);
            stream.Dispose();
            stream = null;

            if (!outcome.Success)
            {
                var mismatch = WindowsProcessProvider.IsArchitectureMismatch(
                    Environment.Is64BitOperatingSystem, Environment.Is64BitProcess, WindowsProcessProvider.IsWow64(request.ProcessId));
                var (status, message) = ClassifyFailure(outcome.Error, ct.IsCancellationRequested, mismatch);
                return Failure(status, message);
            }

            handedOver = true; // from here on the finalizer owns the files and removes them if it fails
            var finalized = await _finalizer.FinalizeAsync(new FinalizeRequest(
                rawPath, finalPath, BuildKeySources(request), request.ProcessId, processName, expectedStart, request.Type,
                RawDumpCheck: DumpVerifier.VerifyWithDbgHelp, Progress: request.Progress), ct);

            return finalized.Succeeded
                ? new DumpResult(DumpStatus.Succeeded, finalPath, finalized.Message, finalized.Metadata, decision, warnings)
                : Failure(finalized.Status, finalized.Message ?? "The dump could not be finished.");
        }
        catch (OperationCanceledException)
        {
            return Failure(DumpStatus.Cancelled, "The dump was cancelled; the incomplete file was removed.");
        }
        catch (IOException e)
        {
            return Failure(DumpStatus.Failed, $"Could not write the dump file: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            return Failure(DumpStatus.AccessDenied, $"The output location is not writable: {e.Message}");
        }
        finally
        {
            stream?.Dispose();
            if (!handedOver) Core.Security.RestrictedFile.SecureDelete(rawPath);
        }
    }

    /// <summary>Runs the native call on a worker thread while reporting how many bytes have reached the file.</summary>
    private async Task<NativeDumpOutcome> WriteNativeAsync(DumpRequest request, SafeProcessHandle process, FileStream stream, CancellationToken ct)
    {
        using var stopPolling = new CancellationTokenSource();
        var poller = request.Progress is null
            ? Task.CompletedTask
            : PollProgressAsync(stream.SafeFileHandle, request.Progress, stopPolling.Token);
        try
        {
            var file = stream.SafeFileHandle;
            return await Task.Run(() => _writer.Write(process, request.ProcessId, file, request.Type, ct), ct);
        }
        finally
        {
            stopPolling.Cancel();
            await poller;
        }
    }

    private List<IKekSource> BuildKeySources(DumpRequest request)
    {
        var sources = new List<IKekSource>();
        if (request.Encrypt) sources.Add(_machineKey!);
        if (request.Password is { } password) sources.Add(new PasswordKekSource(password));
        return sources;
    }

    /// <summary>
    /// Maps the error from a failed MiniDumpWriteDump to a status. DbgHelp reports HRESULTs, so values of the form
    /// 0x8007xxxx are unwrapped to the Win32 code they carry.
    /// </summary>
    public static (DumpStatus Status, string Message) ClassifyFailure(int error, bool cancellationRequested, bool architectureMismatch)
    {
        var win32 = ((uint)error & 0xFFFF0000) == 0x80070000 ? error & 0xFFFF : error;

        if (cancellationRequested || win32 == ErrorCancelled)
            return (DumpStatus.Cancelled, "The dump was cancelled; the incomplete file was removed.");
        if (win32 == ErrorAccessDenied)
            return (DumpStatus.AccessDenied, "Windows denied access while reading the target process's memory.");
        if (win32 == ErrorPartialCopy && architectureMismatch)
            return (DumpStatus.ArchitectureMismatch,
                "ProcLens is running as a 32-bit process and the target is 64-bit; run the 64-bit build to dump it.");
        if (win32 == ErrorPartialCopy)
            return (DumpStatus.Failed,
                "Part of the target's memory could not be read (ERROR_PARTIAL_COPY). The process may have exited during the dump, or it is protected.");
        if (win32 == ErrorDiskFull)
            return (DumpStatus.Failed, "There is not enough disk space for the dump.");

        return (DumpStatus.Failed, $"MiniDumpWriteDump failed: {new Win32Exception(win32).Message} (0x{error:X8}).");
    }

    /// <returns>A description of the identity problem, or null if the handle still refers to the expected process.</returns>
    private static string? VerifyIdentity(SafeProcessHandle process, DateTimeOffset expectedStart)
    {
        if (!ProcessTimes.GetExitCodeProcess(process, out var exitCode) || exitCode != ProcessTimes.StillActive)
            return "The process exited before the dump started.";

        if (!ProcessTimes.GetProcessTimes(process, out var creation, out _, out _, out _))
            return "The process start time could not be read, so its identity cannot be verified.";

        var actual = DateTimeOffset.FromFileTime(creation);
        return Math.Abs((actual - expectedStart).TotalMilliseconds) < 50
            ? null
            : "The process with this id is not the one that was inspected (its id was reused); the dump was not taken.";
    }

    private static async Task PollProgressAsync(SafeFileHandle file, IProgress<DumpProgress> progress, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                progress.Report(new DumpProgress(RandomAccess.GetLength(file)));
        }
        catch (OperationCanceledException)
        {
            // expected when the dump finishes
        }
    }

    private static DumpResult Fail(
        DumpStatus status, string message, AuthorizationDecision? decision = null, IReadOnlyList<string>? warnings = null) =>
        new(status, Message: message, Authorization: decision, Warnings: warnings);
}
