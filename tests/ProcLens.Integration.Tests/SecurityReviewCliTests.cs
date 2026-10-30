using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProcLens.Cli;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Infrastructure.Audit;

namespace ProcLens.Integration.Tests;

[Collection("CLI")]
public class SecurityReviewCliTests : CliTestBase
{
    private static AuditRecord Record(string op) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "PC\\alice", false, "PC", op, null, null, null, null, null, null,
        new AuditAuthorization(true, "owner", "ok"), AuditOutcome.Succeeded, null, null, null, null);

    // ---------- audit anchors ----------

    [Fact]
    public async Task Audit_verify_prints_the_head_hash_and_accepts_it_back_as_an_anchor()
    {
        var log = new FileAuditLog(AuditFile);
        await log.AppendAsync(Record("a"));
        await log.AppendAsync(Record("b"));

        var first = await Run("audit", "verify");
        first.Code.Should().Be(0);
        var head = log.ReadAll()[^1].Hash;
        first.Out.Should().Contain($"Head hash: {head}");

        await log.AppendAsync(Record("c"));   // the log grows after the anchor was saved: still fine
        (await Run("audit", "verify", "--anchor", head)).Code.Should().Be(0);
    }

    [Fact]
    public async Task A_log_cut_short_or_rewritten_fails_the_anchor_check_although_every_remaining_record_is_valid()
    {
        var log = new FileAuditLog(AuditFile);
        for (var i = 0; i < 4; i++) await log.AppendAsync(Record($"op{i}"));
        var anchor = log.ReadAll()[3].Hash;

        File.WriteAllLines(AuditFile, File.ReadAllLines(AuditFile).Take(2));   // the newest records are removed

        (await Run("audit", "verify")).Code.Should().Be(0, "without an anchor the shortened log looks fine; that is the documented limit");
        var anchored = await Run("audit", "verify", "--anchor", anchor);
        anchored.Code.Should().Be(5);
        anchored.Err.Should().Contain("anchor");
    }

    // ---------- policy show ----------

    [Fact]
    public async Task Policy_show_reports_an_explicitly_chosen_policy_as_in_use()
    {
        File.WriteAllText(PolicyFile, """{ "AdminDumpsEnabled": true, "AllowedOperators": ["CORP\\ops"], "Retention": { "MaxAgeDays": 7, "MaxCount": 3 } }""");

        var (code, output, _) = await Run("policy", "show");

        code.Should().Be(0);
        output.Should().Contain("in use (path chosen with PROCLENS_POLICY)").And.Contain("enabled:            True")
            .And.Contain(@"CORP\ops").And.Contain("max age:            7 day(s)").And.Contain("max count:          3");
    }

    [Fact]
    public async Task Policy_show_with_no_file_says_defaults_apply_and_json_is_valid()
    {
        var text = await Run("policy", "show");
        var json = await Run("policy", "show", "--json");

        text.Out.Should().Contain("absent (defaults apply)").And.Contain("enabled:            False").And.Contain("max age:            30 day(s)");
        using var doc = JsonDocument.Parse(json.Out);
        doc.RootElement.GetProperty("Exists").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("Admin").GetProperty("AdminDumpsEnabled").GetBoolean().Should().BeFalse();
    }

    // ---------- unexpected failures ----------

    private sealed class ExplodingProvider : IPlatformProcessProvider
    {
        public PlatformKind Platform => PlatformKind.Linux;
        public IReadOnlyList<CapabilityInfo> GetCapabilities() => CapabilityDefaults.InspectionAndMonitoring();
        public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default) => throw new InvalidOperationException("internal state is corrupt");
        public Task<ProcessInfo?> GetProcessInfoAsync(int processId, CancellationToken ct = default) => throw new InvalidOperationException("internal state is corrupt");
        public Task<MemoryStats?> GetMemoryUsageAsync(int processId, CancellationToken ct = default) => throw new InvalidOperationException("x");
        public Task<InspectionResult<ThreadInfo>> GetThreadsAsync(int processId, CancellationToken ct = default) => throw new InvalidOperationException("x");
        public Task<InspectionResult<ModuleInfo>> GetModulesAsync(int processId, CancellationToken ct = default) => throw new InvalidOperationException("x");
    }

    private static async Task<(int Code, string Out, string Err)> RunWithBrokenProvider(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApplication.RunAsync(args, o, e, default, services =>
        {
            services.AddSingleton<IPlatformProcessProvider>(new ExplodingProvider());
            services.AddSingleton<ICapabilityRegistry>(sp => new CapabilityRegistry(PlatformKind.Linux, CapabilityDefaults.InspectionAndMonitoring()));
        });
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_one_line_error_with_a_distinct_exit_code_and_no_stack_trace()
    {
        var (code, output, error) = await RunWithBrokenProvider("process", "list");

        code.Should().Be(CliApplication.UnexpectedErrorExitCode).And.Be(70);
        output.Should().BeEmpty();
        error.Should().Contain("Unexpected error (InvalidOperationException): internal state is corrupt").And.Contain("PROCLENS_DEBUG=1");
        error.Should().NotContain("   at ", "stack traces expose internals and paths");
    }

    [Fact]
    public async Task The_full_details_are_available_on_request()
    {
        Environment.SetEnvironmentVariable("PROCLENS_DEBUG", "1");
        try
        {
            var (_, _, error) = await RunWithBrokenProvider("process", "list");

            error.Should().Contain("System.InvalidOperationException").And.Contain("   at ");
        }
        finally { Environment.SetEnvironmentVariable("PROCLENS_DEBUG", null); }
    }

    // ---------- report output path ----------

    [Fact]
    public async Task A_report_cannot_be_written_into_an_alternate_data_stream_or_a_device()
    {
        if (!OperatingSystem.IsWindows()) return;

        var stream = await Run("report", "generate", "--pid", Environment.ProcessId.ToString(), "--samples", "1", "--output", Path.Combine(Root, "host.txt") + ":hidden");
        var device = await Run("report", "generate", "--pid", Environment.ProcessId.ToString(), "--samples", "1", "--output", "NUL");

        stream.Code.Should().Be(2);
        stream.Err.Should().Contain("alternate data stream");
        device.Code.Should().Be(2);
        device.Err.Should().Contain("device");
        Directory.GetFiles(Root).Should().NotContain(f => Path.GetFileName(f) == "host.txt");
    }
}
