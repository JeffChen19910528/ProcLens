using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ProcLens.Cli;
using ProcLens.Cli.Hosting;
using ProcLens.Cli.Localization;

namespace ProcLens.Integration.Tests;

[Collection("CLI")]
public partial class LocalizationTests : CliTestBase
{
    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    private static async Task<(int Code, string Out, string Err, CatalogTranslator? Translator)> RunIn(string language, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        CatalogTranslator? translator = null;
        var code = await CliApplication.RunAsync([.. args, "--lang", language], o, e, translatorCreated: t => translator = t as CatalogTranslator);
        return (code, o.ToString(), e.ToString(), translator);
    }

    private static Dictionary<string, string> ReadCatalog()
    {
        using var stream = typeof(CatalogTranslator).Assembly.GetManifestResourceStream("ProcLens.Cli.Localization.Catalogs.zh-TW.json")!;
        using var document = JsonDocument.Parse(stream);
        var entries = new Dictionary<string, string>();
        foreach (var property in document.RootElement.EnumerateObject())
            entries.Add(property.Name, property.Value.GetString()!); // Add throws on a duplicate key
        return entries;
    }

    // ---------- the catalog itself ----------

    [Fact]
    public void Every_translation_keeps_exactly_the_placeholders_of_its_english_text()
    {
        foreach (var (english, chinese) in ReadCatalog())
        {
            chinese.Should().NotBeNullOrWhiteSpace($"'{english}' needs a translation");
            var wanted = Placeholder().Matches(english).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
            var got = Placeholder().Matches(chinese).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
            got.Should().Equal(wanted, $"the translation of '{english}' must use the same {{n}} placeholders");
        }
    }

    [Fact]
    public void Every_english_text_in_the_catalog_formats_cleanly_in_both_languages()
    {
        // string.Format throws on a brace the translator forgot to double; catch that here, not in front of a user.
        foreach (var (english, chinese) in ReadCatalog())
        {
            var count = Placeholder().Matches(english).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
            var args = Enumerable.Range(0, count).Select(i => (object?)$"v{i}").ToArray();
            var act = () => (string.Format(english, args), string.Format(chinese, args));
            act.Should().NotThrow($"'{english}' must be a valid format string");
        }
    }

    [Fact]
    public void Messages_from_lower_layers_are_translated_including_the_parts_inside_them()
    {
        var translator = CatalogTranslator.Load(Languages.TraditionalChinese);

        translator.Localize("Process 4242 was not found or has exited.").Should().Be("找不到處理程序 4242，或它已結束。");
        translator.Localize("Access denied: Reading /proc/<pid>/maps was refused.")
            .Should().Be("存取被拒：讀取 /proc/<pid>/maps 被拒絕。");
        translator.Localize("The target process is owned by 'bob', not by the operator 'alice'. Only processes you own can be dumped.")
            .Should().Contain("bob").And.Contain("alice").And.NotContain("owned by");
        translator.Misses.Should().BeEmpty();
    }

    [Fact]
    public void Text_that_is_not_in_the_catalog_stays_english_and_is_reported_as_a_miss()
    {
        var translator = CatalogTranslator.Load(Languages.TraditionalChinese);

        translator.Localize("Some brand new sentence.").Should().Be("Some brand new sentence.");
        translator.Format("Another new template {0}.", ["x"]).Should().Be("Another new template x.");
        translator.Misses.Should().BeEquivalentTo("Some brand new sentence.", "Another new template {0}.");
    }

    // ---------- choosing the language ----------

    [Fact]
    public void The_language_is_english_unless_something_asks_otherwise()
    {
        LanguageSettings.Resolve([]).Should().Be(new LanguageChoice("en", "default"));
    }

    [Fact]
    public void The_command_line_beats_the_environment_which_beats_the_saved_setting()
    {
        LanguageSettings.Save("zh-TW");
        LanguageSettings.Resolve([]).Should().Be(new LanguageChoice("zh-TW", "config"));

        Environment.SetEnvironmentVariable(LanguageSettings.EnvironmentVariable, "en");
        LanguageSettings.Resolve([]).Should().Be(new LanguageChoice("en", LanguageSettings.EnvironmentVariable));

        LanguageSettings.Resolve(["process", "list", "--lang", "zh-TW"]).Should().Be(new LanguageChoice("zh-TW", "--lang"));
        LanguageSettings.Resolve(["--lang=zh_tw"]).Language.Should().Be("zh-TW");
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english_and_says_so()
    {
        var choice = LanguageSettings.Resolve(["--lang", "klingon"]);

        choice.Language.Should().Be("en");
        choice.Problem.Should().Contain("klingon").And.Contain("zh-TW");
    }

    [Fact]
    public async Task A_damaged_config_file_never_stops_the_tool()
    {
        File.WriteAllText(ConfigFile, "{ this is not json");

        var run = await Run("system", "info");

        run.Code.Should().Be(0, run.Err);
    }

    [Fact]
    public async Task Saving_a_language_changes_later_runs_and_auto_clears_it()
    {
        var save = await Run("config", "set-language", "zh-TW");
        save.Code.Should().Be(0, save.Err);
        save.Out.Should().Contain("已儲存顯示語言");

        (await Run("config", "show")).Out.Should().Contain("語言：").And.Contain("zh-TW");

        var clear = await Run("config", "set-language", "auto");
        clear.Code.Should().Be(0, clear.Err);
        (await Run("config", "show")).Out.Should().Contain("Language:").And.Contain("default");

        var bad = await Run("config", "set-language", "klingon");
        bad.Code.Should().Be(2);
        bad.Err.Should().Contain("klingon");
    }

    [Fact]
    public async Task The_language_option_is_accepted_by_every_command()
    {
        foreach (var args in new[] { new[] { "system", "info" }, new[] { "policy", "show" }, new[] { "audit", "show" }, new[] { "config", "show" } })
        {
            var run = await RunIn("zh-TW", args);
            run.Code.Should().Be(0, $"{string.Join(' ', args)}: {run.Err}");
        }
    }

    // ---------- scenarios ----------

    private static Process StartTarget()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-t 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "300");
        info.CreateNoWindow = true;
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        return Process.Start(info)!;
    }

    [Fact]
    public async Task Every_text_a_person_can_see_in_the_battery_of_scenarios_is_translated_and_nothing_else_changes()
    {
        using var target = StartTarget();
        try
        {
            await Task.Delay(1000);
            var pid = target.Id.ToString();
            var dumps = Path.Combine(Root, "dumps");
            Environment.SetEnvironmentVariable(PasswordVariable, "correct horse battery staple");

            var scenarios = new List<string[]>
            {
                new[] { "process", "list", "--name", "ping" },
                new[] { "process", "inspect", "--pid", pid },
                new[] { "process", "inspect", "--pid", "2147483000" },
                new[] { "process", "monitor", "--pid", pid, "--count", "2", "--interval", "0.2" },
                new[] { "process", "monitor", "--pid", "2147483000", "--count", "1" },
                new[] { "process", "threads", "--pid", pid },
                new[] { "process", "threads", "--pid", "2147483000" },
                new[] { "process", "modules", "--pid", pid },
                new[] { "system", "info" },
                new[] { "system", "capabilities" },
                new[] { "policy", "show" },
                new[] { "audit", "show" },
                new[] { "audit", "verify" },
                new[] { "config", "show" },
                new[] { "report", "generate", "--pid", pid, "--samples", "2", "--interval", "0.2" },
                new[] { "report", "generate", "--pid", pid, "--samples", "2", "--interval", "0.2", "--format", "html" },
                new[] { "report", "generate", "--pid", "2147483000" },
                new[] { "dump", "create", "--pid", pid, "--output", dumps, "--password-env", PasswordVariable },
                new[] { "dump", "create", "--pid", "2147483000", "--output", dumps },
                new[] { "dump", "info", "--file", Path.Combine(Root, "missing.dmp") },
                new[] { "dump", "prune", "--directory", dumps },
                new[] { "dump", "prune", "--directory", Path.Combine(Root, "nowhere") },
                new[] { "process", "inspect" },
                new[] { "process", "list", "--sort", "nonsense" },
                new[] { "unknown-command" },
            };

            var untranslated = new SortedSet<string>();
            foreach (var args in scenarios)
            {
                var english = await RunIn("en", args);
                var chinese = await RunIn("zh-TW", args);

                chinese.Code.Should().Be(english.Code, $"the language must never change the exit code of: {string.Join(' ', args)}\n{chinese.Err}");
                foreach (var miss in chinese.Translator!.Misses) untranslated.Add($"{string.Join(' ', args)}  =>  {miss}");
            }

            untranslated.Should().BeEmpty("every sentence shown to a person needs an entry in zh-TW.json");
        }
        finally
        {
            target.Kill();
            target.WaitForExit();
        }
    }

    [Fact]
    public async Task Json_output_is_english_and_identical_whatever_the_display_language()
    {
        foreach (var args in new[]
                 {
                     new[] { "system", "capabilities", "--json" },
                     new[] { "policy", "show", "--json" },
                     new[] { "audit", "verify", "--json" },
                 })
        {
            var english = await RunIn("en", args);
            var chinese = await RunIn("zh-TW", args);

            chinese.Out.Should().Be(english.Out, $"JSON must not depend on the language: {string.Join(' ', args)}");
            chinese.Out.Should().NotMatchRegex(@"[　-鿿]", "no Chinese in machine-readable output");
        }
    }

    [Fact]
    public async Task Config_json_reports_the_active_language_in_english_terms()
    {
        var run = await RunIn("zh-TW", "config", "show", "--json");

        var json = JsonDocument.Parse(run.Out).RootElement;
        json.GetProperty("Language").GetString().Should().Be("zh-TW");
        json.GetProperty("Source").GetString().Should().Be("--lang");
        run.Out.Should().NotMatchRegex(@"[　-鿿]");
    }

    [Fact]
    public async Task The_chinese_text_view_really_is_chinese_and_tables_stay_aligned()
    {
        var run = await RunIn("zh-TW", "process", "list", "--name", Process.GetCurrentProcess().ProcessName);

        run.Code.Should().Be(0, run.Err);
        var lines = run.Out.TrimEnd().Split('\n');
        lines[0].Should().Contain("記憶體").And.Contain("執行緒").And.Contain("名稱");

        // Wide characters take two columns, so the header must end the NAME column exactly where the data's names start.
        var nameColumn = Display.Width(lines[0][..lines[0].IndexOf("名稱", StringComparison.Ordinal)]);
        foreach (var row in lines.Skip(1))
            Display.Width(row[..row.IndexOf(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)]).Should().Be(nameColumn);
    }

    [Fact]
    public async Task Reports_speak_chinese_in_txt_and_html_but_keep_ids_and_json_unchanged()
    {
        var self = Environment.ProcessId.ToString();
        var args = new[] { "report", "generate", "--pid", self, "--samples", "2", "--interval", "0.2", "--no-modules" };

        var txt = await RunIn("zh-TW", [.. args, "--format", "txt"]);
        txt.Code.Should().Be(0, txt.Err);
        txt.Out.Should().Contain("ProcLens 診斷報告").And.Contain("處理程序").And.Contain("工作集").And.Contain("[可用]");
        txt.Translator!.Misses.Should().BeEmpty();

        var html = await RunIn("zh-TW", [.. args, "--format", "html"]);
        html.Code.Should().Be(0, html.Err);
        html.Out.Should().Contain("<html lang=\"zh-TW\">").And.Contain("ProcLens 診斷報告").And.Contain("處理程序");
        // Anchors are identifiers, not words: they must be the same in every language.
        Regex.Matches(html.Out, @"<section id=""([a-z]+)"">").Select(m => m.Groups[1].Value)
            .Should().Equal("system", "process", "cpu", "memory", "threads", "modules", "dumps", "warnings", "errors");
        html.Translator!.Misses.Should().BeEmpty();

        // JSON is the data model: identical apart from times and live numbers, and never translated.
        var json = await RunIn("zh-TW", [.. args, "--format", "json"]);
        json.Code.Should().Be(0, json.Err);
        json.Out.Should().NotMatchRegex(@"[　-鿿]");
        JsonDocument.Parse(json.Out).RootElement.GetProperty("Process").GetProperty("Status").GetString().Should().Be("Available");
    }

    [Fact]
    public async Task Report_tables_in_chinese_keep_their_columns_aligned()
    {
        var run = await RunIn("zh-TW", "report", "generate", "--pid", Environment.ProcessId.ToString(), "--samples", "1", "--format", "txt");

        run.Code.Should().Be(0, run.Err);
        var lines = run.Out.Split('\n');
        var header = lines.First(l => l.Contains("執行緒識別碼"));
        var firstRow = lines[Array.IndexOf(lines, header) + 2];

        // The second column starts at the same terminal column in the header and in the data rows below it.
        var secondColumnInRow = Regex.Match(firstRow, @"^(\s+\S+\s{2,})\S").Groups[1].Value;
        Display.Width(header[..header.IndexOf("狀態", StringComparison.Ordinal)]).Should().Be(Display.Width(secondColumnInRow));
    }

    [Fact]
    public async Task Audit_records_and_dump_metadata_stay_english()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var target = StartTarget();
        try
        {
            await Task.Delay(1000);
            Environment.SetEnvironmentVariable(PasswordVariable, "correct horse battery staple");

            var dump = await RunIn("zh-TW", "dump", "create", "--pid", target.Id.ToString(), "--output", Path.Combine(Root, "dumps"), "--password-env", PasswordVariable);
            dump.Code.Should().Be(0, dump.Err);

            var audit = File.ReadAllText(AuditFile);
            audit.Should().NotMatchRegex(@"[　-鿿]", "the audit trail must read the same for every investigator");
            foreach (var sidecar in Directory.GetFiles(Path.Combine(Root, "dumps"), "*.json"))
                File.ReadAllText(sidecar).Should().NotMatchRegex(@"[　-鿿]");
        }
        finally
        {
            target.Kill();
            target.WaitForExit();
        }
    }
}
