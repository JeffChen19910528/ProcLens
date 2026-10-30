using FluentAssertions;
using ProcLens.Cli;

namespace ProcLens.Integration.Tests;

[Collection("CLI")]
public class CliTests : CliTestBase
{

    [Fact]
    public async Task Capabilities_reflect_what_is_implemented_and_never_advertise_dump_off_windows()
    {
        var (code, output, _) = await Run("system", "capabilities");

        code.Should().Be(0);
        output.Should().Contain("ProcessInspection: Supported");
        output.Should().Contain("CpuMonitoring: Supported").And.Contain("MemoryMonitoring: Supported");
        output.Should().Contain("ThreadInspection: Supported");
        output.Should().Contain(OperatingSystem.IsMacOS() ? "ModuleInspection: Not Supported" : "ModuleInspection: Supported");
        output.Should().Contain(OperatingSystem.IsWindows() ? "ProcessDump: Supported" : "ProcessDump: Not Supported");
    }

    [Fact]
    public async Task Process_inspect_json_for_current_process_is_valid()
    {
        var (code, output, _) = await Run("process", "inspect", "--pid", Environment.ProcessId.ToString(), "--json");

        code.Should().Be(0);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        doc.RootElement.GetProperty("ProcessId").GetInt32().Should().Be(Environment.ProcessId);
        doc.RootElement.GetProperty("CpuUsage").GetProperty("IsAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Process_inspect_unknown_pid_fails_with_exit_code_1()
    {
        var (code, _, err) = await Run("process", "inspect", "--pid", int.MaxValue.ToString());

        code.Should().Be(1);
        err.Should().Contain("not found");
    }

    [Fact]
    public async Task Process_list_sorted_by_cpu_measures_usage_for_processes_it_can_read()
    {
        var (code, output, _) = await Run("process", "list", "--sort", "cpu", "--json");

        code.Should().Be(0);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var self = doc.RootElement.EnumerateArray().Single(p => p.GetProperty("ProcessId").GetInt32() == Environment.ProcessId);
        self.GetProperty("CpuUsage").GetProperty("IsAvailable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Process_monitor_emits_one_json_object_per_sample_with_cpu_unavailable_only_on_the_first()
    {
        var (code, output, _) = await Run("process", "monitor", "--pid", Environment.ProcessId.ToString(),
            "--interval", "0.2", "--count", "3", "--json");

        code.Should().Be(0);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        var cpu = lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("CpuPercentOfOneCore").GetProperty("IsAvailable").GetBoolean());
        cpu.Should().Equal(false, true, true);
    }

    [Fact]
    public async Task Process_monitor_unknown_pid_fails_with_exit_code_1()
    {
        var (code, _, err) = await Run("process", "monitor", "--pid", int.MaxValue.ToString(), "--count", "1");

        code.Should().Be(1);
        err.Should().Contain("not found");
    }

    [Fact]
    public async Task Process_threads_json_lists_the_current_process_threads()
    {
        var (code, output, _) = await Run("process", "threads", "--pid", Environment.ProcessId.ToString(), "--json");

        code.Should().Be(0);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        doc.RootElement.GetProperty("Status").GetString().Should().Be("Available");
        doc.RootElement.GetProperty("Items").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Process_modules_json_reports_modules_or_an_explicit_unsupported_status()
    {
        var (code, output, err) = await Run("process", "modules", "--pid", Environment.ProcessId.ToString(), "--json");

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        if (OperatingSystem.IsMacOS())
        {
            code.Should().Be(2);
            doc.RootElement.GetProperty("Status").GetString().Should().Be("NotSupported");
        }
        else
        {
            code.Should().Be(0, err);
            doc.RootElement.GetProperty("Items").GetArrayLength().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Process_threads_and_modules_for_unknown_pid_report_not_found()
    {
        var threads = await Run("process", "threads", "--pid", int.MaxValue.ToString());
        threads.Code.Should().Be(1);
        threads.Err.Should().Contain("not found");

        var modules = await Run("process", "modules", "--pid", int.MaxValue.ToString());
        modules.Code.Should().Be(OperatingSystem.IsMacOS() ? 2 : 1);
    }

    [Fact]
    public async Task Process_monitor_rejects_too_small_an_interval()
    {
        var (code, _, _) = await Run("process", "monitor", "--pid", "1", "--interval", "0");

        code.Should().NotBe(0);
    }

    // ---------- dump ----------

    private const string NotSupportedText = "Process dump is not supported on this operating system.";

    [Fact]
    public async Task Dump_create_is_rejected_off_windows_with_the_specified_message()
    {
        if (OperatingSystem.IsWindows()) return;

        var (code, output, err) = await Run("dump", "create", "--pid", Environment.ProcessId.ToString(), "--type", "full", "--output", Path.GetTempPath());

        code.Should().Be(2);
        output.Should().BeEmpty();
        err.Replace("\r", "").Should().Contain(NotSupportedText + "\nSupported platform: Windows.");
    }

    [Fact]
    public async Task Dump_create_off_windows_never_writes_anything()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "proclens-nodump-" + Guid.NewGuid().ToString("N"));

        await Run("dump", "create", "--pid", Environment.ProcessId.ToString(), "--output", dir);

        Directory.Exists(dir).Should().BeFalse("an unsupported platform must not even create the output directory");
    }

    [Fact]
    public async Task Dump_info_is_rejected_off_windows()
    {
        if (OperatingSystem.IsWindows()) return;

        var (code, _, err) = await Run("dump", "info", "--file", "whatever.dmp");

        code.Should().Be(2);
        err.Should().Contain(NotSupportedText);
    }

    [Fact]
    public async Task Dump_create_and_info_round_trip_on_windows_and_tampering_is_detected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "proclens-cli-" + Guid.NewGuid().ToString("N"));
        using var target = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            await Task.Delay(500);

            var (code, output, err) = await Run("dump", "create", "--pid", target.Id.ToString(), "--type", "mini", "--output", dir, "--no-encrypt", "--json");
            code.Should().Be(0, err);
            using var created = System.Text.Json.JsonDocument.Parse(output);
            created.RootElement.GetProperty("Status").GetString().Should().Be("Succeeded");
            var file = created.RootElement.GetProperty("FilePath").GetString()!;
            File.Exists(file).Should().BeTrue();

            var info = await Run("dump", "info", "--file", file);
            info.Code.Should().Be(0, info.Err);
            info.Out.Should().Contain("Integrity:  OK").And.Contain("Dump type:  Mini").And.Contain("Valid:      yes");

            await File.AppendAllBytesAsync(file, [0x00]);
            var tampered = await Run("dump", "info", "--file", file);
            tampered.Code.Should().Be(5);
            tampered.Out.Should().Contain("MISMATCH");
        }
        finally
        {
            target.Kill();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Dump_create_error_exit_codes_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "proclens-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            (await Run("dump", "create", "--pid", int.MaxValue.ToString(), "--output", dir)).Code.Should().Be(1);
            (await Run("dump", "create", "--pid", Environment.ProcessId.ToString(), "--output", dir)).Code.Should().Be(2, "ProcLens refuses to dump itself");
            (await Run("dump", "create", "--pid", "4", "--output", dir)).Code.Should().Be(3, "the System process is not accessible");
            Directory.Exists(dir).Should().BeFalse("no request got far enough to need the directory");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Dump_info_on_a_non_dump_file_reports_it_invalid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var junk = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(junk, "this is not a minidump, just some text that is long enough to read a header from");

            var (code, output, _) = await Run("dump", "info", "--file", junk);

            code.Should().Be(5);
            output.Should().Contain("Valid:      NO");
        }
        finally { File.Delete(junk); }
    }
}
