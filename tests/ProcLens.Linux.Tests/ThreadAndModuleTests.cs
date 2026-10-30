using System.Diagnostics;
using FluentAssertions;
using ProcLens.Core.Models;
using ProcLens.Platform.Linux;

namespace ProcLens.Linux.Tests;

public class ThreadAndModuleParsingTests
{
    private const string Maps = """
        00400000-00452000 r-xp 00000000 08:01 1234                               /usr/bin/foo
        00651000-00652000 r--p 00051000 08:01 1234                               /usr/bin/foo
        7f0000000000-7f0000100000 r-xp 00000000 08:01 99                         /usr/lib/libc.so.6
        7f0000100000-7f0000200000 ---p 00100000 08:01 99                         /usr/lib/libc.so.6
        7f0000300000-7f0000301000 r--s 00000000 08:01 55                         /var/lib/data.db
        7f0000400000-7f0000401000 rw-p 00000000 00:00 0
        7f0000500000-7f0000501000 r-xp 00000000 08:01 7                          /opt/my app/lib weird.so
        7ffc00000000-7ffc00001000 rw-p 00000000 00:00 0                          [stack]
        7ffc00002000-7ffc00003000 r-xp 00000000 00:00 0                          [vdso]
        """;

    [Fact]
    public void ParseMaps_returns_executable_file_backed_images_ordered_by_address()
    {
        var modules = ProcParser.ParseMaps(Maps);

        modules.Select(m => m.Name).Should().Equal("foo", "libc.so.6", "lib weird.so");
    }

    [Fact]
    public void ParseMaps_uses_lowest_start_and_total_span_as_base_and_size()
    {
        var modules = ProcParser.ParseMaps(Maps);
        var foo = modules.First(m => m.Name == "foo");
        var libc = modules.First(m => m.Name == "libc.so.6");

        foo.BaseAddress.Value.Should().Be(0x400000UL);
        foo.Size.Value.Should().Be(0x652000 - 0x400000);
        libc.BaseAddress.Value.Should().Be(0x7f0000000000UL);
        libc.Size.Value.Should().Be(0x200000);
        foo.Path.Should().Be("/usr/bin/foo");
    }

    [Fact]
    public void ParseMaps_excludes_data_files_anonymous_and_pseudo_mappings()
    {
        var paths = ProcParser.ParseMaps(Maps).Select(m => m.Path).ToList();

        paths.Should().NotContain("/var/lib/data.db", "it has no executable mapping");
        paths.Should().NotContain(p => p!.StartsWith("["));
    }

    [Fact]
    public void ParseMaps_keeps_paths_that_contain_spaces() =>
        ProcParser.ParseMaps(Maps).Should().Contain(m => m.Path == "/opt/my app/lib weird.so");

    [Fact]
    public void ParseMaps_ignores_malformed_lines_and_empty_input()
    {
        ProcParser.ParseMaps("").Should().BeEmpty();
        ProcParser.ParseMaps("garbage\nzz-yy r-xp 0 0 0 /x\n").Should().BeEmpty();
    }

    [Theory]
    [InlineData('R', ThreadRunState.Running)]
    [InlineData('S', ThreadRunState.Sleeping)]
    [InlineData('D', ThreadRunState.Waiting)]
    [InlineData('T', ThreadRunState.Stopped)]
    [InlineData('t', ThreadRunState.Stopped)]
    [InlineData('Z', ThreadRunState.Terminated)]
    [InlineData('?', ThreadRunState.Unknown)]
    public void MapThreadState_maps_kernel_state_letters(char letter, ThreadRunState expected) =>
        ProcParser.MapThreadState(letter).State.Should().Be(expected);
}

public class ThreadAndModuleFakeTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proclens-" + Guid.NewGuid().ToString("N"));

    public ThreadAndModuleFakeTreeTests()
    {
        var proc = Directory.CreateDirectory(Path.Combine(_root, "77")).FullName;
        foreach (var (tid, state, name) in new[] { (77, 'S', "main"), (80, 'R', "worker"), (79, 'D', "io") })
        {
            var dir = Directory.CreateDirectory(Path.Combine(proc, "task", tid.ToString())).FullName;
            File.WriteAllText(Path.Combine(dir, "stat"), $"{tid} ({name}) {state} 5 77 77 0 -1 0 0 0 0 0 150 50 0 0 20 0 3 0 500 0 0");
        }
        File.WriteAllText(Path.Combine(proc, "maps"), "00400000-00452000 r-xp 00000000 08:01 1 /usr/bin/foo\n");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Threads_are_listed_sorted_by_id_with_state_name_and_cpu_time()
    {
        var result = await new LinuxProcessProvider(_root).GetThreadsAsync(77);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Select(t => t.ThreadId).Should().Equal(77, 79, 80);
        result.Items.Select(t => t.Name).Should().Equal("main", "io", "worker");
        result.Items.Select(t => t.State).Should().Equal(ThreadRunState.Sleeping, ThreadRunState.Waiting, ThreadRunState.Running);
        result.Items.Should().OnlyContain(t => t.CpuTime.Value == TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Modules_come_from_maps()
    {
        var result = await new LinuxProcessProvider(_root).GetModulesAsync(77);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Should().ContainSingle().Which.Name.Should().Be("foo");
    }

    [Fact]
    public async Task Unknown_process_is_reported_as_not_found_for_threads_and_modules()
    {
        var provider = new LinuxProcessProvider(_root);

        (await provider.GetThreadsAsync(999)).Status.Should().Be(InspectionStatus.ProcessNotFound);
        (await provider.GetModulesAsync(999)).Status.Should().Be(InspectionStatus.ProcessNotFound);
    }

    [Fact]
    public async Task Unreadable_maps_is_access_denied_not_an_empty_module_list()
    {
        // A directory where the maps file should be makes File.ReadAllText fail.
        File.Delete(Path.Combine(_root, "77", "maps"));
        Directory.CreateDirectory(Path.Combine(_root, "77", "maps"));

        var result = await new LinuxProcessProvider(_root).GetModulesAsync(77);

        result.Status.Should().Be(InspectionStatus.AccessDenied);
        result.Items.Should().BeEmpty();
    }
}

public class ThreadAndModuleLiveTests
{
    [SkippableOnNonLinux]
    public async Task Threads_of_this_process_include_the_main_thread_and_overlap_the_runtime_view()
    {
        using var self = Process.GetCurrentProcess();

        var result = await new LinuxProcessProvider().GetThreadsAsync(self.Id);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Should().Contain(t => t.ThreadId == self.Id, "the main thread's TID equals the PID");
        result.Items.Select(t => t.ThreadId).Intersect(self.Threads.Cast<ProcessThread>().Select(t => t.Id)).Should().NotBeEmpty();
        result.Items.Should().OnlyContain(t => t.CpuTime.IsAvailable);
    }

    [SkippableOnNonLinux]
    public async Task Modules_of_this_process_include_the_runtime_and_the_host_binary()
    {
        var result = await new LinuxProcessProvider().GetModulesAsync(Environment.ProcessId);

        result.Status.Should().Be(InspectionStatus.Available);
        result.Items.Should().Contain(m => m.Name.StartsWith("libcoreclr"));
        result.Items.Should().Contain(m => m.Name == Path.GetFileName(Environment.ProcessPath));
        result.Items.Should().OnlyContain(m => m.BaseAddress.Value > 0 && m.Size.Value > 0);
    }

    [SkippableOnNonLinux]
    public async Task Another_users_process_modules_are_denied_not_empty()
    {
        if (Environment.UserName == "root") return; // root may read everything; nothing to assert

        var result = await new LinuxProcessProvider().GetModulesAsync(1);

        result.Status.Should().Be(InspectionStatus.AccessDenied);
        result.Reason.Should().NotBeNullOrEmpty();
        result.Items.Should().BeEmpty();
    }
}
