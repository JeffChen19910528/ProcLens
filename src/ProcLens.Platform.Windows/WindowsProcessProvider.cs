using System.ComponentModel;
using System.Diagnostics;
using ThreadState = System.Diagnostics.ThreadState;
using System.Security;
using System.Security.Principal;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using static ProcLens.Core.Models.Probe;
using ProcLens.Platform.Windows.Native;

namespace ProcLens.Platform.Windows;

public sealed class WindowsProcessProvider : IPlatformProcessProvider
{
    public PlatformKind Platform => PlatformKind.Windows;

    public IReadOnlyList<CapabilityInfo> GetCapabilities() => CapabilityDefaults.InspectionAndMonitoring();

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProcessInfo>>(() =>
        {
            var parents = SnapshotParentIds();
            var result = new List<ProcessInfo>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    ct.ThrowIfCancellationRequested();
                    result.Add(Build(process, parents));
                }
            }
            return result;
        }, ct);

    public Task<ProcessInfo?> GetProcessInfoAsync(int processId, CancellationToken ct = default) =>
        Task.Run<ProcessInfo?>(() =>
        {
            using var process = Open(processId);
            return process is null ? null : Build(process, SnapshotParentIds());
        }, ct);

    public Task<MemoryStats?> GetMemoryUsageAsync(int processId, CancellationToken ct = default) =>
        Task.Run<MemoryStats?>(() =>
        {
            using var process = Open(processId);
            if (process is null) return null;
            return new MemoryStats(
                Read(() => process.WorkingSet64),
                Read(() => process.PrivateMemorySize64),
                Read(() => process.VirtualMemorySize64),
                Read(() => process.PeakWorkingSet64));
        }, ct);

    public Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var process = Open(processId);
            if (process is null) return InspectionResult<ThreadInfo>.NotFound();
            try
            {
                var threads = new List<ThreadInfo>();
                foreach (ProcessThread thread in process.Threads)
                {
                    ct.ThrowIfCancellationRequested();
                    threads.Add(ToThreadInfo(thread));
                }
                return InspectionResult<ThreadInfo>.Ok(threads.OrderBy(t => t.ThreadId).ToList());
            }
            catch (InvalidOperationException)
            {
                return InspectionResult<ThreadInfo>.NotFound(); // exited while enumerating
            }
            catch (Win32Exception e)
            {
                return InspectionResult<ThreadInfo>.Denied(e.Message);
            }
        }, ct);

    public Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var process = Open(processId);
            if (process is null) return InspectionResult<ModuleInfo>.NotFound();
            try
            {
                var modules = new List<ModuleInfo>();
                foreach (ProcessModule module in process.Modules)
                {
                    ct.ThrowIfCancellationRequested();
                    modules.Add(new ModuleInfo(
                        module.ModuleName,
                        module.FileName,
                        Measurement<ulong>.Of((ulong)module.BaseAddress),
                        Measurement<long>.Of(module.ModuleMemorySize)));
                }
                return InspectionResult<ModuleInfo>.Ok(modules);
            }
            catch (InvalidOperationException)
            {
                return InspectionResult<ModuleInfo>.NotFound();
            }
            catch (Win32Exception e)
            {
                // A 32-bit inspector cannot read a 64-bit target's modules. That is not a permission problem,
                // so report it separately; everything else is treated as access denied.
                if (IsArchitectureMismatch(Environment.Is64BitOperatingSystem, Environment.Is64BitProcess, IsWow64(processId)))
                    return InspectionResult<ModuleInfo>.ArchitectureMismatch(
                        "ProcLens is running as a 32-bit process and the target is 64-bit; run the 64-bit build to list its modules.");

                return InspectionResult<ModuleInfo>.Denied($"{e.Message} (Win32 error {e.NativeErrorCode})");
            }
        }, ct);

    /// <summary>
    /// True when a 32-bit inspector on a 64-bit OS faces a 64-bit target (<paramref name="targetIsWow64"/> == false).
    /// The reverse (64-bit inspector, 32-bit target) works and is not a mismatch. Unknown bitness is never a mismatch.
    /// </summary>
    public static bool IsArchitectureMismatch(bool osIs64Bit, bool callerIs64Bit, bool? targetIsWow64) =>
        osIs64Bit && !callerIs64Bit && targetIsWow64 == false;

    /// <summary>True if the target is a 32-bit process on 64-bit Windows; null if its bitness cannot be determined.</summary>
    internal static bool? IsWow64(int processId)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid) return null;
        return NativeMethods.IsWow64Process(handle, out var wow64) ? wow64 : null;
    }

    private static ThreadInfo ToThreadInfo(ProcessThread thread)
    {
        var state = TryGet(() => thread.ThreadState);
        var cpu = TryGet(() => thread.TotalProcessorTime);
        var mapped = state switch
        {
            ThreadState.Running => ThreadRunState.Running,
            ThreadState.Ready or ThreadState.Standby or ThreadState.Initialized or ThreadState.Transition => ThreadRunState.Ready,
            ThreadState.Wait => ThreadRunState.Waiting,
            ThreadState.Terminated => ThreadRunState.Terminated,
            _ => ThreadRunState.Unknown,
        };
        var detail = state == ThreadState.Wait ? TryGet(() => thread.WaitReason)?.ToString() : null;

        return new ThreadInfo(
            thread.Id, Name: null, mapped, detail,
            cpu.HasValue ? Measurement<TimeSpan>.Of(cpu.Value) : Measurement<TimeSpan>.Unavailable(UnavailableReasons.AccessDenied));
    }

    /// <summary>Null if the process does not exist or has already exited.</summary>
    private static Process? Open(int processId)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return null; }

        if (TryGet(() => process.HasExited) == true)
        {
            process.Dispose();
            return null;
        }
        return process;
    }

    private static ProcessInfo Build(Process process, IReadOnlyDictionary<int, int> parents)
    {
        var startTime = TryGet(() => (DateTimeOffset)process.StartTime);
        var cpuTime = TryGet(() => process.TotalProcessorTime);
        var workingSet = TryGet(() => process.WorkingSet64);
        var threads = TryGet(() => process.Threads.Count);
        var path = QueryImagePath(process.Id);
        var owner = QueryOwner(process.Id);

        var readable = new[] { startTime.HasValue, workingSet.HasValue, threads.HasValue, path is not null, owner is not null };
        var access = readable.All(r => r) ? AccessStatus.Full
            : readable.Any(r => r) ? AccessStatus.Partial
            : AccessStatus.Denied;

        return new ProcessInfo(
            ProcessId: process.Id,
            ProcessName: process.ProcessName,
            ExecutablePath: path,
            ParentProcessId: parents.TryGetValue(process.Id, out var ppid) ? ppid : null,
            Owner: owner,
            StartTime: startTime,
            CpuUsage: Measurement<double>.Unavailable(UnavailableReasons.CpuNotSampled),
            CpuTime: cpuTime.HasValue ? Measurement<TimeSpan>.Of(cpuTime.Value) : Measurement<TimeSpan>.Unavailable(UnavailableReasons.AccessDenied),
            MemoryUsage: workingSet.HasValue ? Measurement<long>.Of(workingSet.Value) : Measurement<long>.Unavailable(UnavailableReasons.AccessDenied),
            ThreadCount: threads.HasValue ? Measurement<int>.Of(threads.Value) : Measurement<int>.Unavailable(UnavailableReasons.AccessDenied),
            Platform: PlatformKind.Windows,
            AccessStatus: access);
    }

    private static Measurement<long> Read(Func<long> read)
    {
        var value = TryGet(read);
        return value.HasValue ? Measurement<long>.Of(value.Value) : Measurement<long>.Unavailable(UnavailableReasons.AccessDenied);
    }

    private static string? QueryImagePath(int processId)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid) return null;

        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
            ? new string(buffer, 0, (int)size)
            : null;
    }

    /// <summary>Resolves DOMAIN\user from the process token; null if the token cannot be opened (e.g. protected processes).</summary>
    internal static string? QueryOwner(int processId)
    {
        using var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid) return null;
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token)) return null;

        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                return identity.User?.Translate(typeof(NTAccount)).Value ?? identity.User?.Value;
            }
            catch (Exception e) when (e is IdentityNotMappedException or SecurityException or ArgumentException)
            {
                return null;
            }
        }
    }

    private static Dictionary<int, int> SnapshotParentIds()
    {
        var map = new Dictionary<int, int>();
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot.IsInvalid) return map;

        var entry = new ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry32>() };
        for (var ok = NativeMethods.Process32FirstW(snapshot, ref entry); ok; ok = NativeMethods.Process32NextW(snapshot, ref entry))
            map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        return map;
    }
}
