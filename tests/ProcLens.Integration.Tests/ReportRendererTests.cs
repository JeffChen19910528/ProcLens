using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Core.Reports;
using ProcLens.Infrastructure.Reports;

namespace ProcLens.Integration.Tests;

public class ReportRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static ProcessInfo Process(string name = "demo", string? path = "/bin/demo", string? owner = "alice", AccessStatus access = AccessStatus.Full) => new(
        4242, name, path, 1, owner, At.AddHours(-2), Measurement<double>.Unavailable("not sampled"),
        Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(12.5)), Measurement<long>.Of(100 * 1024 * 1024), Measurement<int>.Of(3),
        PlatformKind.Linux, access);

    private static DiagnosticReport Report(
        string platform = "Linux", ProcessInfo? process = null, Section<CpuSummary>? cpu = null, Section<MemoryStats>? memory = null,
        Section<ThreadSummary>? threads = null, Section<IReadOnlyList<ModuleInfo>>? modules = null,
        Section<IReadOnlyList<DumpMetadata>>? dumps = null, IReadOnlyList<DiagnosticWarning>? warnings = null,
        IReadOnlyList<CollectionError>? errors = null) => new(
        new ReportHeader(1, At, "1.2.3"),
        new SystemSection(platform, "TestOS 1.0", "X64", 8, ".NET 10", "HOST",
            [new CapabilityInfo(Capability.ProcessInspection, SupportStatus.Supported),
             new CapabilityInfo(Capability.ProcessDump, SupportStatus.NotSupported, "Windows only")]),
        Section<ProcessInfo>.Ok(process ?? Process()),
        cpu ?? Section<CpuSummary>.Ok(new CpuSummary(3, TimeSpan.FromSeconds(1), 8, Measurement<TimeSpan>.Of(TimeSpan.FromSeconds(12.5)),
            Measurement<double>.Of(0), Measurement<double>.Of(0), Measurement<double>.Of(0))),
        memory ?? Section<MemoryStats>.Ok(new MemoryStats(
            Measurement<long>.Of(1024 * 1024), Measurement<long>.Unavailable("Not available on Linux."), Measurement<long>.Of(0), Measurement<long>.Of(2048 * 1024))),
        threads ?? Section<ThreadSummary>.Ok(new ThreadSummary(1, new Dictionary<ThreadRunState, int> { [ThreadRunState.Sleeping] = 1 },
            [new ThreadInfo(10, "worker", ThreadRunState.Sleeping, "idle", Measurement<TimeSpan>.Of(TimeSpan.Zero))])),
        modules ?? Section<IReadOnlyList<ModuleInfo>>.Ok([new ModuleInfo("libc.so", "/lib/libc.so", Measurement<ulong>.Of(0xDEAD0000), Measurement<long>.Of(4096))]),
        dumps ?? Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.NotCollected, "No dump directory was given (use --dumps-dir)."),
        warnings ?? [], errors ?? []);

    private static string Render(string format, DiagnosticReport report) => ReportRenderers.For(format)!.Render(report);

    // ---------- JSON ----------

    [Fact]
    public void Json_is_valid_has_the_fixed_top_level_shape_and_keeps_reasons_and_statuses()
    {
        using var doc = JsonDocument.Parse(Render("json", Report()));
        var root = doc.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().Equal("Header", "System", "Process", "Cpu", "Memory", "Threads", "Modules", "Dumps", "Warnings", "Errors");
        root.GetProperty("Header").GetProperty("SchemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("Process").GetProperty("Status").GetString().Should().Be("Available");
        root.GetProperty("Dumps").GetProperty("Status").GetString().Should().Be("NotCollected");
        root.GetProperty("Dumps").GetProperty("Reason").GetString().Should().Contain("--dumps-dir");
        var memory = root.GetProperty("Memory").GetProperty("Data");
        memory.GetProperty("PrivateMemory").GetProperty("IsAvailable").GetBoolean().Should().BeFalse();
        memory.GetProperty("PrivateMemory").GetProperty("UnavailableReason").GetString().Should().Contain("Linux");
        memory.GetProperty("VirtualMemory").GetProperty("IsAvailable").GetBoolean().Should().BeTrue("a real zero is a value");
        memory.GetProperty("VirtualMemory").GetProperty("Value").GetInt64().Should().Be(0);
    }

    [Fact]
    public void Json_text_with_quotes_and_newlines_stays_valid() =>
        FluentActions.Invoking(() => JsonDocument.Parse(Render("json", Report(process: Process(name: "a\"b\nc\\d\u0001"))))).Should().NotThrow();

    // ---------- text ----------

    [Fact]
    public void Text_has_every_section_in_the_fixed_order_with_a_status_each()
    {
        var text = Render("txt", Report());

        var headings = Regex.Matches(text, @"^([A-Z]+)  \[(.+)\]$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        headings.Should().Equal(DiagnosticReport.SectionNames.Select(n => n.ToUpperInvariant()));
    }

    [Fact]
    public void Unavailable_values_say_n_a_with_the_reason_and_real_zeros_stay_zero()
    {
        var text = Render("txt", Report());

        text.Should().Contain("Private memory   : n/a (Not available on Linux.)");
        text.Should().Contain("Virtual memory   : 0 B");
        text.Should().Contain("Average, % of whole machine : 0.0%");
        text.Should().NotContain("Private memory   : 0");
    }

    [Fact]
    public void Missing_values_for_the_process_itself_are_n_a_not_blank()
    {
        var text = Render("txt", Report(process: Process(path: null, owner: null) with { ParentProcessId = null, StartTime = null }));

        text.Should().Contain("Executable             : n/a (not readable)")
            .And.Contain("Owner                  : n/a (not readable)")
            .And.Contain("Parent process id      : n/a (not reported)")
            .And.Contain("Started                : n/a (not readable)")
            .And.Contain("Cumulative CPU time    : 00:12.500");
    }

    [Fact]
    public void Each_section_status_is_rendered_distinctly()
    {
        var text = Render("txt", Report(
            cpu: Section<CpuSummary>.Missing(SectionStatus.NotSupported, "no sampler"),
            memory: Section<MemoryStats>.Missing(SectionStatus.AccessDenied, "denied"),
            threads: Section<ThreadSummary>.Missing(SectionStatus.NotCollected, "not asked"),
            modules: Section<IReadOnlyList<ModuleInfo>>.Missing(SectionStatus.ArchitectureMismatch, "bitness"),
            dumps: Section<IReadOnlyList<DumpMetadata>>.Missing(SectionStatus.Failed, "boom")));

        text.Should().Contain("CPU  [Not supported]").And.Contain("MEMORY  [Access denied]").And.Contain("THREADS  [Not collected]")
            .And.Contain("MODULES  [Architecture mismatch]").And.Contain("DUMPS  [Failed]");
        text.Should().Contain("no sampler").And.Contain("denied").And.Contain("bitness").And.Contain("boom");
    }

    [Fact]
    public void Text_cannot_be_made_to_forge_lines_or_sections_through_a_hostile_process_name()
    {
        var benign = Render("txt", Report(process: Process(name: "evil")));
        var hostile = Render("txt", Report(process: Process(name: "evil\r\n\nPROCESS  [Available]\u001b[31m‮txt.exe")));

        hostile.Split('\n').Length.Should().Be(benign.Split('\n').Length, "control characters must not add lines");
        hostile.Should().NotContain("\u001b").And.NotContain("‮").And.NotContain("\r");
        Regex.Matches(hostile, @"^PROCESS  \[", RegexOptions.Multiline).Count.Should().Be(1);
    }

    [Fact]
    public void Empty_tables_are_left_out_but_their_counts_are_kept()
    {
        var text = Render("txt", Report(threads: Section<ThreadSummary>.Ok(new ThreadSummary(0, new Dictionary<ThreadRunState, int>(), []))));

        text.Should().Contain("Count    : 0").And.Contain("By state : none");
        text.Should().NotContain("Thread id");
    }

    [Fact]
    public void Warnings_and_errors_are_listed_or_explicitly_empty()
    {
        var none = Render("txt", Report());
        var some = Render("txt", Report(
            warnings: [new DiagnosticWarning("HIGH_CPU", "busy")],
            errors: [new CollectionError("Modules", SectionStatus.AccessDenied, "denied")]));

        none.Should().Contain("No warnings.").And.Contain("No collection errors");
        some.Should().Contain("WARNINGS  [1 warning(s)]").And.Contain("HIGH_CPU  busy");
        some.Should().Contain("ERRORS  [1 collection error(s)]").And.Contain("Modules  Access denied");
    }

    // ---------- HTML ----------

    [Fact]
    public void Html_is_self_contained_with_no_scripts_external_resources_or_event_handlers()
    {
        var html = Render("html", Report());

        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("Content-Security-Policy").And.Contain("default-src 'none'");
        html.Should().NotContainEquivalentOf("<script").And.NotContainEquivalentOf("javascript:").And.NotContain("http://").And.NotContain("https://");
        Regex.IsMatch(html, @"\son\w+\s*=", RegexOptions.IgnoreCase).Should().BeFalse("no inline event handlers");
        Regex.IsMatch(html, @"<(link|img|iframe|object|embed|form)\b", RegexOptions.IgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void Html_encodes_hostile_process_data_everywhere_it_appears()
    {
        const string evil = "<script>alert(1)</script>\"'&<img src=x onerror=alert(2)>";
        var html = Render("html", Report(
            process: Process(name: evil, path: evil, owner: evil),
            modules: Section<IReadOnlyList<ModuleInfo>>.Ok([new ModuleInfo(evil, evil, Measurement<ulong>.Of(1), Measurement<long>.Of(1))]),
            threads: Section<ThreadSummary>.Ok(new ThreadSummary(1, new Dictionary<ThreadRunState, int> { [ThreadRunState.Running] = 1 },
                [new ThreadInfo(1, evil, ThreadRunState.Running, evil, Measurement<TimeSpan>.Of(TimeSpan.Zero))])),
            warnings: [new DiagnosticWarning("X", evil)],
            errors: [new CollectionError("Modules", SectionStatus.Failed, evil)]));

        html.Should().NotContainEquivalentOf("<script").And.NotContain("<img").And.NotContain("onerror=alert(2)>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        Regex.IsMatch(html, @"<[^>]*\sonerror\s*=", RegexOptions.IgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void Html_has_the_same_sections_as_the_text_report_in_the_same_order()
    {
        var report = Report();
        var html = Render("html", report);

        Regex.Matches(html, @"<section id=""([a-z]+)"">").Select(m => m.Groups[1].Value)
            .Should().Equal(DiagnosticReport.SectionNames.Select(n => n.ToLowerInvariant()));
        Regex.Matches(html, @"<main>").Count.Should().Be(1);
    }

    [Fact]
    public void Html_and_text_state_the_same_facts()
    {
        var report = Report();
        var view = ReportViewBuilder.Build(report);
        var text = Render("txt", report);
        var html = Render("html", report);

        foreach (var fact in view.Sections.SelectMany(s => s.Facts))
        {
            text.Should().Contain(fact.Value);
            html.Should().Contain(System.Net.WebUtility.HtmlEncode(fact.Value));
        }
    }

    [Fact]
    public void Html_marks_statuses_with_text_not_only_colour_and_supports_dark_mode()
    {
        var html = Render("html", Report(cpu: Section<CpuSummary>.Missing(SectionStatus.AccessDenied, "denied")));

        html.Should().Contain(">Access denied</span>").And.Contain("prefers-color-scheme:dark").And.Contain("name=\"viewport\"");
    }

    // ---------- consistency across platforms ----------

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void The_structure_is_identical_whatever_the_platform(string platform)
    {
        var report = Report(platform: platform);

        var text = Render("txt", report);
        var headings = Regex.Matches(text, @"^([A-Z]+)  \[", RegexOptions.Multiline).Select(m => m.Groups[1].Value);
        using var json = JsonDocument.Parse(Render("json", report));

        headings.Should().Equal(DiagnosticReport.SectionNames.Select(n => n.ToUpperInvariant()));
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("Header", "System", "Process", "Cpu", "Memory", "Threads", "Modules", "Dumps", "Warnings", "Errors");
        text.Should().Contain($"Platform           : {platform}");
    }

    [Fact]
    public void Unsupported_sections_look_the_same_whichever_platform_lacks_them()
    {
        var gap = Section<IReadOnlyList<ModuleInfo>>.Missing(SectionStatus.NotSupported, "not available through managed APIs");

        var mac = Render("txt", Report(platform: "MacOS", modules: gap));
        var other = Render("txt", Report(platform: "Linux", modules: gap));

        foreach (var text in new[] { mac, other })
            text.Should().Contain("MODULES  [Not supported]").And.Contain("not available through managed APIs");
        mac.Should().NotContain("No collection errors: 1", "an unsupported section is not an error");
    }

    // ---------- scale ----------

    [Fact]
    public void A_report_with_thousands_of_modules_and_threads_renders_quickly_in_every_format()
    {
        var modules = Enumerable.Range(0, 5000).Select(i => new ModuleInfo($"m{i}.dll", $"/lib/m{i}.dll", Measurement<ulong>.Of((ulong)i), Measurement<long>.Of(i))).ToList();
        var threads = Enumerable.Range(0, 3000).Select(i => new ThreadInfo(i, null, ThreadRunState.Waiting, "x", Measurement<TimeSpan>.Of(TimeSpan.Zero))).ToList();
        var report = Report(modules: Section<IReadOnlyList<ModuleInfo>>.Ok(modules),
            threads: Section<ThreadSummary>.Ok(new ThreadSummary(3000, new Dictionary<ThreadRunState, int> { [ThreadRunState.Waiting] = 3000 }, threads)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sizes = ReportRenderers.All.Select(r => r.Render(report).Length).ToList();
        watch.Stop();

        sizes.Should().OnlyContain(s => s > 100_000);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Renderers_are_found_case_insensitively_and_unknown_formats_are_not()
    {
        ReportRenderers.For("HTML")!.FileExtension.Should().Be(".html");
        ReportRenderers.For("json")!.FileExtension.Should().Be(".json");
        ReportRenderers.For("txt")!.FileExtension.Should().Be(".txt");
        ReportRenderers.For("pdf").Should().BeNull();
    }
}
