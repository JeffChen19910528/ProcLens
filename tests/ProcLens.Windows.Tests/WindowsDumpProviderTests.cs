using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using Microsoft.Win32.SafeHandles;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Platform.Windows;

namespace ProcLens.Windows.Tests;

/// <summary>A harmless, long-lived 64-bit process owned by the test user, used as a dump target.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TargetProcess : IDisposable
{
    private readonly Process _process;

    public TargetProcess()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        _process = Process.Start(new ProcessStartInfo(ping, "-t 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        Thread.Sleep(500); // let the loader finish so the module list is stable
    }

    public int Id => _process.Id;
    public DateTimeOffset StartTime => _process.StartTime;

    public DumpRequest Request(string directory, DumpType type = DumpType.Mini, IProgress<DumpProgress>? progress = null) =>
        new(Id, type, directory, StartTime, _process.ProcessName, progress);

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { }
        _process.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ScriptedWriter(Func<SafeFileHandle, CancellationToken, NativeDumpOutcome> action) : IMiniDumpWriter
{
    public int Calls;

    public NativeDumpOutcome Write(SafeProcessHandle process, int processId, SafeFileHandle file, DumpType type, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return action(file, ct);
    }
}

internal sealed class DenyAll : IDumpAuthorizer
{
    public AuthorizationDecision Authorize(DumpAuthorizationContext context) => new(false, "denied by test policy");
}

internal sealed class SyncProgress : IProgress<DumpProgress>
{
    public List<long> Reports { get; } = [];
    public void Report(DumpProgress value) { lock (Reports) Reports.Add(value.BytesWritten); }
}

[SupportedOSPlatform("windows")]
public class WindowsDumpProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proclens-dump-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public WindowsDumpProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private TargetProcess Target()
    {
        var t = new TargetProcess();
        _disposables.Add(t);
        return t;
    }

    private string[] Files() => Directory.GetFileSystemEntries(_dir);

    // ---------- real dumps ----------

    [SkippableOnNonWindows]
    public async Task Mini_dump_of_a_real_process_succeeds_with_verified_file_and_metadata()
    {
        var target = Target();

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        var meta = result.Metadata!;
        File.Exists(result.FilePath).Should().BeTrue();
        new FileInfo(result.FilePath!).Length.Should().BeGreaterThan(0).And.Be(meta.FileSize);
        meta.ProcessId.Should().Be(target.Id);
        meta.DumpType.Should().Be(DumpType.Mini);
        meta.CollectionStatus.Should().Be(CollectionStatus.Succeeded);
        meta.Sha256.Should().Be(await DumpFiles.ComputeSha256Async(result.FilePath!));
        DumpFiles.TryReadMetadata(result.FilePath!).Should().Be(meta, "the metadata is also persisted next to the dump");
        MinidumpReader.Read(result.FilePath!).Should().Match<MinidumpInfo>(i => i.IsValid && i.InferredType == DumpType.Mini);
    }

    [SkippableOnNonWindows]
    public async Task Dump_types_produce_increasingly_complete_dumps()
    {
        var target = Target();
        var provider = new WindowsProcessDumpProvider();
        var sizes = new Dictionary<DumpType, long>();

        foreach (var type in new[] { DumpType.Mini, DumpType.Heap, DumpType.Full })
        {
            var result = await provider.CreateDumpAsync(target.Request(_dir, type));
            result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
            MinidumpReader.Read(result.FilePath!).InferredType.Should().Be(type);
            sizes[type] = result.Metadata!.FileSize;
        }

        sizes[DumpType.Heap].Should().BeGreaterThan(sizes[DumpType.Mini]);
        sizes[DumpType.Full].Should().BeGreaterThan(sizes[DumpType.Heap]);
    }

    [SkippableOnNonWindows]
    public async Task A_full_dump_can_be_opened_by_an_independent_tool_and_contains_readable_memory()
    {
        var target = Target();
        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir, DumpType.Full));
        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);

        // ClrMD parses minidumps with its own implementation, so this is not just ProcLens agreeing with itself.
        using var dataTarget = DataTarget.LoadDump(result.FilePath!);
        var reader = dataTarget.DataReader;

        reader.ProcessId.Should().Be(target.Id);
        reader.Architecture.Should().Be(Architecture.X64);
        var modules = reader.EnumerateModules().ToList();
        modules.Should().Contain(m => m.FileName != null && m.FileName.EndsWith("ping.exe", StringComparison.OrdinalIgnoreCase));
        reader.Should().BeAssignableTo<IThreadReader>().Which.EnumerateOSThreadIds().Should().NotBeEmpty();

        var exe = modules.First(m => m.FileName!.EndsWith("ping.exe", StringComparison.OrdinalIgnoreCase));
        var header = new byte[2];
        reader.Read(exe.ImageBase, header).Should().Be(2, "a full dump contains the module's image memory");
        header.Should().Equal((byte)'M', (byte)'Z');
    }

    [SkippableOnNonWindows]
    public async Task Dump_file_and_metadata_are_accessible_only_to_the_current_user()
    {
        var target = Target();
        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir));
        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);

        foreach (var path in new[] { result.FilePath!, DumpFiles.SidecarPath(result.FilePath!) })
        {
            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();

            rules.Should().ContainSingle($"{Path.GetFileName(path)} must not grant anyone else access");
            rules[0].IdentityReference.Should().Be(WindowsIdentity.GetCurrent().User);
            rules[0].IsInherited.Should().BeFalse();
        }
    }

    [SkippableOnNonWindows]
    public async Task Progress_ends_with_the_final_file_size()
    {
        var target = Target();
        var progress = new SyncProgress();

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir, DumpType.Heap, progress));

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Last().Should().Be(result.Metadata!.FileSize);
        progress.Reports.Should().BeInAscendingOrder();
    }

    // ---------- request validation and refusal ----------

    [SkippableOnNonWindows]
    public async Task Refuses_to_dump_itself()
    {
        var request = new DumpRequest(Environment.ProcessId, DumpType.Mini, _dir, DateTimeOffset.UtcNow);

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(request);

        result.Status.Should().Be(DumpStatus.InvalidRequest);
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Requires_the_expected_start_time()
    {
        var target = Target();

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(_dir) with { ExpectedStartTime = null });

        result.Status.Should().Be(DumpStatus.InvalidRequest);
    }

    [SkippableOnNonWindows]
    public async Task Unknown_process_is_not_found()
    {
        var request = new DumpRequest(int.MaxValue, DumpType.Mini, _dir, DateTimeOffset.UtcNow);

        (await new WindowsProcessDumpProvider().CreateDumpAsync(request)).Status.Should().Be(DumpStatus.ProcessNotFound);
    }

    [SkippableOnNonWindows]
    public async Task A_reused_pid_is_detected_by_the_start_time_and_nothing_is_written()
    {
        var target = Target();
        var writer = new ScriptedWriter((_, _) => new NativeDumpOutcome(true, 0));
        var wrongIdentity = target.Request(_dir) with { ExpectedStartTime = target.StartTime.AddMinutes(-30) };

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(wrongIdentity);

        result.Status.Should().Be(DumpStatus.ProcessNotFound);
        result.Message.Should().Contain("reused");
        writer.Calls.Should().Be(0);
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Authorization_is_checked_before_anything_is_opened_or_written()
    {
        var target = Target();
        var writer = new ScriptedWriter((_, _) => new NativeDumpOutcome(true, 0));

        var result = await new WindowsProcessDumpProvider(writer, new DenyAll()).CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Unauthorized);
        result.Message.Should().Contain("denied by test policy");
        writer.Calls.Should().Be(0);
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task A_process_whose_owner_cannot_be_read_is_refused_by_the_default_policy()
    {
        // audiodg and similar system processes expose a start time but not an owner to a non-elevated user.
        var candidate = Process.GetProcesses()
            .Select(p => (p, owner: WindowsProcessProvider.QueryOwner(p.Id)))
            .FirstOrDefault(x => x.owner is null && x.p.Id > 4 && x.p.Id != Environment.ProcessId && TryStart(x.p) is not null);
        if (candidate.p is null) return; // no such process on this machine (e.g. an elevated run); nothing to assert

        var request = new DumpRequest(candidate.p.Id, DumpType.Mini, _dir, TryStart(candidate.p));
        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(request);

        result.Status.Should().Be(DumpStatus.Unauthorized);
        Files().Should().BeEmpty();

        static DateTimeOffset? TryStart(Process p)
        {
            try { return p.StartTime; } catch (Exception) { return null; }
        }
    }

    [SkippableOnNonWindows]
    public async Task Unusable_output_locations_are_rejected_before_any_process_access()
    {
        var target = Target();
        var provider = new WindowsProcessDumpProvider(new ScriptedWriter((_, _) => throw new InvalidOperationException("must not run")));

        var file = Path.Combine(_dir, "a-file");
        File.WriteAllText(file, "x");

        (await provider.CreateDumpAsync(target.Request(file))).Status.Should().Be(DumpStatus.InvalidRequest);
        (await provider.CreateDumpAsync(target.Request(@"\\.\C:\dumps"))).Status.Should().Be(DumpStatus.InvalidRequest);
        (await provider.CreateDumpAsync(target.Request(""))).Status.Should().Be(DumpStatus.InvalidRequest);
        (await provider.CreateDumpAsync(target.Request("bad\0path"))).Status.Should().Be(DumpStatus.InvalidRequest);
    }

    [SkippableOnNonWindows]
    public async Task A_junction_as_output_directory_is_rejected()
    {
        var target = Target();
        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        var link = Path.Combine(_dir, "link");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            await mk.WaitForExitAsync();
            if (mk.ExitCode != 0) return; // junction creation not permitted here
        }

        try
        {
            var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(link));

            result.Status.Should().Be(DumpStatus.InvalidRequest);
            result.Message.Should().Contain("junction");
            Directory.GetFiles(real).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(link, recursive: false); // removes only the junction, never its target
        }
    }

    [SkippableOnNonWindows]
    public async Task A_missing_output_directory_is_created()
    {
        var target = Target();
        var nested = Path.Combine(_dir, "a", "b");

        var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(nested));

        result.Status.Should().Be(DumpStatus.Succeeded, result.Message);
        Path.GetDirectoryName(result.FilePath).Should().Be(nested);
    }

    [SkippableOnNonWindows]
    public void Existing_files_are_never_overwritten()
    {
        var path = Path.Combine(_dir, "existing.dmp");
        File.WriteAllText(path, "precious");

        var act = () => SecureFile.CreateNew(path);

        act.Should().Throw<IOException>();
        File.ReadAllText(path).Should().Be("precious");
    }

    // ---------- failure, verification and cleanup ----------

    [SkippableOnNonWindows]
    public async Task A_failed_native_call_leaves_no_partial_dump_or_metadata()
    {
        var target = Target();
        var writer = new ScriptedWriter((file, _) =>
        {
            RandomAccess.Write(file, new byte[4096], 0); // some bytes were already written
            return new NativeDumpOutcome(false, unchecked((int)0x8007012B)); // HRESULT_FROM_WIN32(ERROR_PARTIAL_COPY)
        });

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("PARTIAL_COPY");
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task An_exception_while_dumping_still_cleans_up()
    {
        var target = Target();
        var writer = new ScriptedWriter((file, _) =>
        {
            RandomAccess.Write(file, new byte[100], 0);
            throw new IOException("disk went away");
        });

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("disk went away");
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task An_empty_output_is_a_failure_even_if_the_native_call_claimed_success()
    {
        var target = Target();
        var writer = new ScriptedWriter((_, _) => new NativeDumpOutcome(true, 0));

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("empty");
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Garbage_output_fails_verification_and_is_removed()
    {
        var target = Target();
        var writer = new ScriptedWriter((file, _) =>
        {
            RandomAccess.Write(file, Enumerable.Repeat((byte)0xAB, 512).ToArray(), 0);
            return new NativeDumpOutcome(true, 0);
        });

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(target.Request(_dir));

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("verification");
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Cancellation_while_dumping_returns_cancelled_and_removes_the_partial_file()
    {
        var target = Target();
        using var cts = new CancellationTokenSource();
        var writer = new ScriptedWriter((file, ct) =>
        {
            RandomAccess.Write(file, new byte[8192], 0);
            cts.Cancel(); // the operator presses Ctrl+C mid-dump
            SpinWait.SpinUntil(() => ct.IsCancellationRequested, 2000);
            return new NativeDumpOutcome(false, unchecked((int)0x800704C7)); // HRESULT_FROM_WIN32(ERROR_CANCELLED)
        });

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(target.Request(_dir), cts.Token);

        result.Status.Should().Be(DumpStatus.Cancelled);
        Files().Should().BeEmpty();
    }

    [SkippableOnNonWindows]
    public async Task Already_cancelled_requests_do_nothing()
    {
        var target = Target();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var writer = new ScriptedWriter((_, _) => new NativeDumpOutcome(true, 0));

        var result = await new WindowsProcessDumpProvider(writer).CreateDumpAsync(target.Request(_dir), cts.Token);

        result.Status.Should().Be(DumpStatus.Cancelled);
        writer.Calls.Should().Be(0);
        Files().Should().BeEmpty();
    }

    // ---------- serialization ----------

    private sealed class TrackingWriter(IMiniDumpWriter inner) : IMiniDumpWriter
    {
        private readonly object _lock = new();
        private int _current;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }

        public NativeDumpOutcome Write(SafeProcessHandle process, int processId, SafeFileHandle file, DumpType type, CancellationToken ct)
        {
            lock (_lock) { _current++; Calls++; MaxConcurrent = Math.Max(MaxConcurrent, _current); }
            try
            {
                Thread.Sleep(150); // widen the window in which an overlap would show up
                return inner.Write(process, processId, file, type, ct);
            }
            finally { lock (_lock) _current--; }
        }
    }

    [SkippableOnNonWindows]
    public async Task Concurrent_requests_are_serialized_even_across_provider_instances()
    {
        var a = Target();
        var b = Target();
        var tracker = new TrackingWriter(new NativeMiniDumpWriter());

        var results = await Task.WhenAll(
            Task.Run(() => new WindowsProcessDumpProvider(tracker).CreateDumpAsync(a.Request(_dir))),
            Task.Run(() => new WindowsProcessDumpProvider(tracker).CreateDumpAsync(b.Request(_dir))),
            Task.Run(() => new WindowsProcessDumpProvider(tracker).CreateDumpAsync(a.Request(_dir, DumpType.Heap))));

        results.Should().OnlyContain(r => r.Status == DumpStatus.Succeeded, string.Join("; ", results.Select(r => r.Message)));
        tracker.Calls.Should().Be(3);
        tracker.MaxConcurrent.Should().Be(1, "DbgHelp is not thread-safe");
    }

    [SkippableOnNonWindows]
    public async Task A_request_waiting_for_the_gate_can_be_cancelled()
    {
        var a = Target();
        var b = Target();
        using var release = new ManualResetEventSlim();
        var blocker = new ScriptedWriter((file, _) =>
        {
            release.Wait(5000);
            RandomAccess.Write(file, new byte[10], 0);
            return new NativeDumpOutcome(false, 1);
        });

        var first = Task.Run(() => new WindowsProcessDumpProvider(blocker).CreateDumpAsync(a.Request(_dir)));
        SpinWait.SpinUntil(() => blocker.Calls > 0, 5000);

        using var cts = new CancellationTokenSource(200);
        var second = await new WindowsProcessDumpProvider().CreateDumpAsync(b.Request(_dir), cts.Token);
        release.Set();
        await first;

        second.Status.Should().Be(DumpStatus.Cancelled);
        Files().Should().BeEmpty();
    }

    /// <summary>
    /// Exercises the real DbgHelp cancel callback against a large process. It writes hundreds of MB, so it only runs
    /// when PROCLENS_HEAVY_TESTS=1 is set.
    /// </summary>
    [SkippableOnNonWindows]
    public async Task Native_cancel_callback_stops_a_real_full_dump_and_removes_the_partial_file()
    {
        if (Environment.GetEnvironmentVariable("PROCLENS_HEAVY_TESTS") != "1") return;

        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var big = Process.Start(new ProcessStartInfo(ps, "-NoProfile -Command \"$x = New-Object byte[] 700MB; Start-Sleep 120\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        _disposables.Add(new KillOnDispose(big));
        await Task.Delay(4000); // allocation

        var request = new DumpRequest(big.Id, DumpType.Full, _dir, big.StartTime, "powershell");
        using var cts = new CancellationTokenSource();
        var started = Stopwatch.StartNew();
        var capture = new CapturingWriter(new NativeMiniDumpWriter());
        var task = new WindowsProcessDumpProvider(capture).CreateDumpAsync(request, cts.Token);
        await Task.Delay(300);
        cts.Cancel();

        var result = await task;
        started.Stop();

        result.Status.Should().Be(DumpStatus.Cancelled, result.Message);
        capture.Outcome.Should().NotBeNull();
        capture.Outcome!.Value.Success.Should().BeFalse();
        (capture.Outcome.Value.Error & 0xFFFF).Should().Be(1223, "DbgHelp itself must report ERROR_CANCELLED, proving the cancel callback fired");
        Files().Should().BeEmpty();
    }

    private sealed class CapturingWriter(IMiniDumpWriter inner) : IMiniDumpWriter
    {
        public NativeDumpOutcome? Outcome { get; private set; }

        public NativeDumpOutcome Write(SafeProcessHandle process, int processId, SafeFileHandle file, DumpType type, CancellationToken ct) =>
            (Outcome = inner.Write(process, processId, file, type, ct)).Value;
    }

    private sealed class KillOnDispose(Process p) : IDisposable
    {
        public void Dispose()
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            p.Dispose();
        }
    }

    // ---------- pure logic ----------

    [Fact]
    public void Only_the_documented_minimum_process_rights_are_requested()
    {
        const uint queryInformation = 0x0400, vmRead = 0x0010;

        WindowsProcessDumpProvider.RequiredAccess.Should().Be(queryInformation | vmRead);
    }

    [Fact]
    public void Dump_types_map_to_the_intended_minidump_flags()
    {
        const uint fullMemory = 0x2, handleData = 0x4, privateReadWrite = 0x200;
        var mini = MiniDumpFlags.For(DumpType.Mini);
        var heap = MiniDumpFlags.For(DumpType.Heap);
        var full = MiniDumpFlags.For(DumpType.Full);

        (mini & (fullMemory | privateReadWrite)).Should().Be(0);
        (heap & privateReadWrite).Should().NotBe(0);
        (heap & fullMemory).Should().Be(0);
        (full & fullMemory).Should().NotBe(0);
        new[] { mini, heap, full }.Should().OnlyContain(f => (f & handleData) == 0, "handle data needs more rights and discloses object names");
        FluentActions.Invoking(() => MiniDumpFlags.For((DumpType)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0x80070005, false, false, DumpStatus.AccessDenied)]
    [InlineData(5, false, false, DumpStatus.AccessDenied)]
    [InlineData(0x8007012B, false, false, DumpStatus.Failed)]
    [InlineData(0x8007012B, false, true, DumpStatus.ArchitectureMismatch)]
    [InlineData(0x800704C7, false, false, DumpStatus.Cancelled)]
    [InlineData(1223, false, false, DumpStatus.Cancelled)]
    [InlineData(0x80070070, false, false, DumpStatus.Failed)] // disk full
    [InlineData(0x8007012B, true, false, DumpStatus.Cancelled)] // a requested cancel wins over the error it causes
    [InlineData(0x80004005, false, false, DumpStatus.Failed)]
    public void Native_errors_are_classified(uint error, bool cancelRequested, bool mismatch, DumpStatus expected) =>
        WindowsProcessDumpProvider.ClassifyFailure(unchecked((int)error), cancelRequested, mismatch).Status.Should().Be(expected);

    [Fact]
    public void Disk_full_has_an_actionable_message() =>
        WindowsProcessDumpProvider.ClassifyFailure(unchecked((int)0x80070070), false, false).Message.Should().Contain("disk space");
}
