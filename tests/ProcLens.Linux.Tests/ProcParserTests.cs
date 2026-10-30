using FluentAssertions;
using ProcLens.Platform.Linux;

namespace ProcLens.Linux.Tests;

public class ProcParserTests
{
    [Fact]
    public void ParseStat_handles_spaces_and_parentheses_in_the_command_name()
    {
        // comm = "my (weird) app"; ppid = 42; starttime (field 22) = 987654
        var line = "100 (my (weird) app) S 42 100 100 0 -1 4194560 1 0 0 0 5 3 0 0 20 0 1 0 987654 1000 200 18446744073709551615";

        var stat = ProcParser.ParseStat(line);

        stat.Should().NotBeNull();
        stat!.Name.Should().Be("my (weird) app");
        stat.ParentProcessId.Should().Be(42);
        stat.StartTimeTicks.Should().Be(987654);
        stat.CpuTicks.Should().Be(5 + 3, "utime + stime");
        stat.State.Should().Be('S');
    }

    [Fact]
    public void ParseStat_returns_null_for_truncated_input() =>
        ProcParser.ParseStat("100 (x) S 1").Should().BeNull();

    [Fact]
    public void ParseKilobytes_converts_to_bytes_and_returns_null_when_missing()
    {
        ProcParser.ParseKilobytes("2048 kB").Should().Be(2048L * 1024);
        ProcParser.ParseKilobytes(null).Should().BeNull();
    }

    [Fact]
    public void ResolveUserName_matches_uid_column()
    {
        const string passwd = "root:x:0:0:root:/root:/bin/bash\nalice:x:1000:1000::/home/alice:/bin/sh\n";
        ProcParser.ResolveUserName(passwd, "1000").Should().Be("alice");
        ProcParser.ResolveUserName(passwd, "9999").Should().BeNull();
    }

    [Fact]
    public async Task Provider_reads_a_fake_proc_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "proclens-" + Guid.NewGuid().ToString("N"));
        var pidDir = Directory.CreateDirectory(Path.Combine(root, "77")).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "stat"), "cpu 1 2 3\nbtime 1700000000\n");
            await File.WriteAllTextAsync(Path.Combine(pidDir, "stat"),
                "77 (demo) S 5 77 77 0 -1 0 0 0 0 0 250 50 0 0 20 0 1 0 500 0 0");
            await File.WriteAllTextAsync(Path.Combine(pidDir, "status"), "Name:\tdemo\nUid:\t1000\t1000\t1000\t1000\nVmSize:\t90000 kB\nVmHWM:\t5000 kB\nVmRSS:\t4096 kB\nThreads:\t3\n");

            var info = await new LinuxProcessProvider(root).GetProcessInfoAsync(77);

            info.Should().NotBeNull();
            info!.ProcessName.Should().Be("demo");
            info.ParentProcessId.Should().Be(5);
            info.MemoryUsage.Value.Should().Be(4096L * 1024);
            info.ThreadCount.Value.Should().Be(3);
            info.CpuTime.Value.Should().Be(TimeSpan.FromSeconds(3), "(250 + 50) ticks at 100 Hz");
            info.CpuUsage.IsAvailable.Should().BeFalse("a single read cannot yield a rate");

            var memory = await new LinuxProcessProvider(root).GetMemoryUsageAsync(77);
            memory!.WorkingSet.Value.Should().Be(4096L * 1024);
            memory.VirtualMemory.Value.Should().Be(90000L * 1024);
            memory.PeakWorkingSet.Value.Should().Be(5000L * 1024);
            memory.PrivateMemory.IsAvailable.Should().BeFalse("Linux has no private-bytes equivalent; must not be reported as 0");
            (await new LinuxProcessProvider(root).GetMemoryUsageAsync(78)).Should().BeNull();
            info.StartTime.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000).AddSeconds(5));
            info.ExecutablePath.Should().BeNull("there is no exe link in the fake tree");
        }
        finally { Directory.Delete(root, true); }
    }
}

