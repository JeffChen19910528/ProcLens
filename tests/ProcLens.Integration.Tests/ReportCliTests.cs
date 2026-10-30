using System.Text.Json;
using FluentAssertions;
using ProcLens.Cli;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Reports;

namespace ProcLens.Integration.Tests;

public class ReportCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-reportcli-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApplication.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static string Self => Environment.ProcessId.ToString();

    private static async Task<JsonElement> JsonReport(params string[] extra)
    {
        var args = new[] { "report", "generate", "--pid", Self, "--format", "json", "--samples", "2", "--interval", "0.2" }.Concat(extra).ToArray();
        var (code, output, err) = await Run(args);
        code.Should().Be(0, err);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    [Fact]
    public async Task Every_format_produces_a_report_of_the_current_process()
    {
        var json = await JsonReport();
        json.GetProperty("Process").GetProperty("Data").GetProperty("ProcessId").GetInt32().Should().Be(Environment.ProcessId);
        json.GetProperty("Process").GetProperty("Status").GetString().Should().Be("Available");
        json.GetProperty("Header").GetProperty("SchemaVersion").GetInt32().Should().Be(DiagnosticReport.CurrentSchemaVersion);

        var txt = await Run("report", "generate", "--pid", Self, "--samples", "2", "--interval", "0.2");
        txt.Code.Should().Be(0, txt.Err);
        txt.Out.Should().Contain("ProcLens Diagnostic Report").And.Contain("PROCESS  [Available]").And.Contain($"Process id             : {Environment.ProcessId}");

        var html = await Run("report", "generate", "--pid", Self, "--format", "html", "--samples", "2", "--interval", "0.2");
        html.Code.Should().Be(0, html.Err);
        html.Out.Should().StartWith("<!DOCTYPE html>").And.Contain("<section id=\"process\">");
    }

    [Fact]
    public async Task The_report_has_the_same_sections_on_every_platform()
    {
        var json = await JsonReport();

        json.EnumerateObject().Select(p => p.Name).Should().Equal("Header", "System", "Process", "Cpu", "Memory", "Threads", "Modules", "Dumps", "Warnings", "Errors");
        json.GetProperty("System").GetProperty("Capabilities").GetArrayLength().Should().Be(Enum.GetValues<ProcLens.Core.Platform.Capability>().Length);
        var txt = (await Run("report", "generate", "--pid", Self, "--samples", "1")).Out;
        txt.Should().NotContain("\r", "reports use LF on every platform");
    }

    [Fact]
    public async Task Cpu_and_memory_of_the_running_test_host_are_measured()
    {
        var json = await JsonReport();

        json.GetProperty("Cpu").GetProperty("Status").GetString().Should().Be("Available");
        json.GetProperty("Cpu").GetProperty("Data").GetProperty("Samples").GetInt32().Should().Be(2);
        var memory = json.GetProperty("Memory").GetProperty("Data");
        memory.GetProperty("WorkingSet").GetProperty("IsAvailable").GetBoolean().Should().BeTrue();
        memory.GetProperty("WorkingSet").GetProperty("Value").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task One_sample_cannot_give_cpu_and_says_not_collected_instead_of_zero()
    {
        var (code, output, _) = await Run("report", "generate", "--pid", Self, "--format", "json", "--samples", "1");

        code.Should().Be(0);
        var cpu = JsonDocument.Parse(output).RootElement.GetProperty("Cpu");
        cpu.GetProperty("Status").GetString().Should().Be("NotCollected");
        cpu.GetProperty("Data").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Platform_differences_show_up_as_statuses_never_as_empty_data_or_errors()
    {
        var json = await JsonReport("--dumps-dir", _dir);

        string Status(string section) => json.GetProperty(section).GetProperty("Status").GetString()!;

        json.GetProperty("Errors").GetArrayLength().Should().Be(0, "the platform's own limitations are not collection errors");
        if (OperatingSystem.IsWindows())
        {
            new[] { Status("Threads"), Status("Modules"), Status("Dumps") }.Should().OnlyContain(s => s == "Available");
        }
        else if (OperatingSystem.IsLinux())
        {
            Status("Threads").Should().Be("Available");
            Status("Modules").Should().Be("Available");
            Status("Dumps").Should().Be("NotSupported");
            json.GetProperty("Dumps").GetProperty("Reason").GetString().Should().Contain("Windows");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Status("Modules").Should().Be("NotSupported");
            Status("Dumps").Should().Be("NotSupported");
        }
    }

    [Fact]
    public async Task Options_can_leave_out_threads_and_modules()
    {
        var json = await JsonReport("--no-threads", "--no-modules");

        json.GetProperty("Threads").GetProperty("Status").GetString().Should().Be("NotCollected");
        json.GetProperty("Modules").GetProperty("Status").GetString().Should().Be("NotCollected");
        json.GetProperty("Errors").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Dumps_of_the_process_are_listed_and_unencrypted_ones_are_warned_about()
    {
        if (!OperatingSystem.IsWindows()) return;
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        var path = Path.Combine(_dir, "self.dmp");
        File.WriteAllBytes(path, [1]);
        File.WriteAllText(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(new DumpMetadata(
            Guid.NewGuid(), Environment.ProcessId, "testhost", started, DumpType.Mini, path, 1, DateTimeOffset.UtcNow, "OS", "1",
            CollectionStatus.Succeeded, null, "abc", "def", null)));

        var json = await JsonReport("--dumps-dir", _dir);

        json.GetProperty("Dumps").GetProperty("Data").GetArrayLength().Should().Be(1);
        json.GetProperty("Warnings").EnumerateArray().Select(w => w.GetProperty("Code").GetString()).Should().Contain("UNENCRYPTED_DUMP");
    }

    // ---------- output file ----------

    [Fact]
    public async Task Output_is_written_to_a_new_private_file_and_never_overwrites()
    {
        var file = Path.Combine(_dir, "nested", "report.html");

        var first = await Run("report", "generate", "--pid", Self, "--format", "html", "--output", file, "--samples", "1");
        var second = await Run("report", "generate", "--pid", Self, "--format", "txt", "--output", file, "--samples", "1");

        first.Code.Should().Be(0, first.Err);
        first.Out.Should().BeEmpty("with --output the report goes to the file, not the console");
        first.Err.Should().Contain("Report written to");
        File.ReadAllText(file).Should().StartWith("<!DOCTYPE html>");
        File.ReadAllBytes(file).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte-order mark");
        second.Code.Should().Be(2);
        second.Err.Should().Contain("never overwritten");
        File.ReadAllText(file).Should().StartWith("<!DOCTYPE html>", "the existing file is untouched");

        if (OperatingSystem.IsWindows())
            new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count.Should().Be(1);
        else
            File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---------- errors ----------

    [Fact]
    public async Task An_unknown_process_exits_with_1_and_writes_nothing()
    {
        var file = Path.Combine(_dir, "none.txt");

        var (code, output, err) = await Run("report", "generate", "--pid", int.MaxValue.ToString(), "--output", file);

        code.Should().Be(1);
        output.Should().BeEmpty();
        err.Should().Contain("was not found");
        File.Exists(file).Should().BeFalse();
    }

    [Theory]
    [InlineData("--format", "pdf")]
    [InlineData("--samples", "0")]
    [InlineData("--samples", "1000")]
    [InlineData("--interval", "0")]
    public async Task Invalid_options_are_rejected_before_anything_is_collected(string option, string value)
    {
        var (code, output, _) = await Run("report", "generate", "--pid", Self, option, value);

        code.Should().NotBe(0);
        output.Should().BeEmpty();
    }
}
