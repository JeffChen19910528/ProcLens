using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;

namespace ProcLens.Core.Tests;

public class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
    private const long Mb = 1024 * 1024;

    private static ManagedDump Dump(string name, double daysOld, long bytes = Mb) => new(
        $"/d/{name}.dmp.enc", $"/d/{name}.dmp.enc.json",
        new DumpMetadata(Guid.NewGuid(), 1, name, Now, DumpType.Mini, $"/d/{name}.dmp.enc", bytes, Now.AddDays(-daysOld), "OS", "1",
            CollectionStatus.Succeeded, null, "h", "p", null),
        bytes);

    private static string[] Names(IReadOnlyList<RetentionCandidate> plan) => plan.Select(c => c.Dump.Metadata.ProcessName).ToArray();

    [Fact]
    public void Age_rule_selects_only_dumps_strictly_older_than_the_limit()
    {
        var plan = RetentionPlanner.Plan([Dump("old", 31), Dump("edge", 30), Dump("new", 1)], new RetentionPolicy(MaxAgeDays: 30), Now);

        Names(plan).Should().Equal("old");
        plan[0].Reason.Should().Contain("older than 30 day");
    }

    [Fact]
    public void Zero_days_makes_every_dump_eligible()
    {
        var plan = RetentionPlanner.Plan([Dump("a", 2), Dump("b", 0.01)], new RetentionPolicy(MaxAgeDays: 0), Now);

        Names(plan).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void Count_rule_keeps_the_newest_n()
    {
        var dumps = new[] { Dump("d1", 1), Dump("d2", 2), Dump("d3", 3), Dump("d4", 4) };

        var plan = RetentionPlanner.Plan(dumps, new RetentionPolicy(MaxAgeDays: 365, MaxCount: 2), Now);

        Names(plan).Should().Equal("d4", "d3");
        plan.Should().OnlyContain(c => c.Reason.Contains("newest 2"));
    }

    [Fact]
    public void Count_zero_removes_everything()
    {
        RetentionPlanner.Plan([Dump("a", 1), Dump("b", 2)], new RetentionPolicy(365, MaxCount: 0), Now).Should().HaveCount(2);
    }

    [Fact]
    public void Size_rule_keeps_the_newest_dumps_that_fit_and_removes_all_older_ones()
    {
        // 10 MB each, limit 25 MB: the two newest fit (20), the third would make 30.
        var dumps = new[] { Dump("n1", 1, 10 * Mb), Dump("n2", 2, 10 * Mb), Dump("n3", 3, 10 * Mb), Dump("n4", 4, 1 * Mb) };

        var plan = RetentionPlanner.Plan(dumps, new RetentionPolicy(365, MaxTotalBytes: 25 * Mb), Now);

        Names(plan).Should().Equal("n4", "n3");
        plan.Should().OnlyContain(c => c.Reason.Contains("25 MB"));
    }

    [Fact]
    public void Size_rule_does_not_count_dumps_that_the_age_rule_already_removes()
    {
        var dumps = new[] { Dump("ancient", 90, 100 * Mb), Dump("recent", 1, 10 * Mb) };

        var plan = RetentionPlanner.Plan(dumps, new RetentionPolicy(30, MaxTotalBytes: 15 * Mb), Now);

        Names(plan).Should().Equal("ancient");
    }

    [Fact]
    public void Rules_combine_and_report_the_first_that_applied()
    {
        var dumps = new[] { Dump("ancient", 90), Dump("a", 1), Dump("b", 2), Dump("c", 3) };

        var plan = RetentionPlanner.Plan(dumps, new RetentionPolicy(30, MaxCount: 2), Now);

        Names(plan).Should().Equal("ancient", "c");
        plan[0].Reason.Should().Contain("older than");
        plan[1].Reason.Should().Contain("newest 2");
    }

    [Fact]
    public void Candidates_are_ordered_oldest_first()
    {
        var plan = RetentionPlanner.Plan([Dump("m", 50), Dump("o", 90), Dump("n", 40)], new RetentionPolicy(30), Now);

        Names(plan).Should().Equal("o", "m", "n");
    }

    [Fact]
    public void A_dump_dated_in_the_future_is_never_selected_by_age()
    {
        // A clock that was wrong when the dump was made must not cause an early deletion.
        RetentionPlanner.Plan([Dump("skewed", -5)], new RetentionPolicy(MaxAgeDays: 0), Now).Should().BeEmpty("it is not older than now");
        RetentionPlanner.Plan([Dump("skewed", -5)], new RetentionPolicy(MaxAgeDays: 30), Now).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_directory_or_a_permissive_policy_plans_nothing()
    {
        RetentionPlanner.Plan([], new RetentionPolicy(0, MaxCount: 0), Now).Should().BeEmpty();
        RetentionPlanner.Plan([Dump("a", 1)], new RetentionPolicy(30, MaxCount: 10, MaxTotalBytes: 100 * Mb), Now).Should().BeEmpty();
    }
}

public class RetentionPolicyLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-ret-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Defaults_apply_when_there_is_no_policy_file_or_no_retention_section()
    {
        RetentionPolicy.Load(Path.Combine(_dir, "none.json")).Should().Match<RetentionPolicy>(p => p.MaxAgeDays == 30 && p.MaxCount == null && p.MaxTotalBytes == null);
        RetentionPolicy.Load(Write("""{ "AdminDumpsEnabled": true }""")).MaxAgeDays.Should().Be(30);
    }

    [Fact]
    public void The_retention_section_is_read_case_insensitively_and_shares_the_admin_policy_file()
    {
        var path = Write("""
            { "AdminDumpsEnabled": true,
              "retention": { "maxagedays": 7, "MaxCount": 3, "MaxTotalMegabytes": 512 } }
            """);

        var policy = RetentionPolicy.Load(path);

        policy.MaxAgeDays.Should().Be(7);
        policy.MaxCount.Should().Be(3);
        policy.MaxTotalBytes.Should().Be(512L * 1024 * 1024);
        policy.Source.Should().Be(path);
        AdminDumpPolicy.Load(path).AdminDumpsEnabled.Should().BeTrue("both policies live in the same file");
    }

    [Fact]
    public void A_partial_section_fills_the_rest_with_defaults()
    {
        var policy = RetentionPolicy.Load(Write("""{ "Retention": { "MaxCount": 5 } }"""));

        policy.MaxAgeDays.Should().Be(30);
        policy.MaxCount.Should().Be(5);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "Retention": { "MaxAgeDays": -1 } }""")]
    [InlineData("""{ "Retention": { "MaxCount": -5 } }""")]
    [InlineData("""{ "Retention": "soon" }""")]
    public void Invalid_policies_fall_back_to_the_safe_defaults_instead_of_deleting_or_keeping_everything(string content)
    {
        var policy = RetentionPolicy.Load(Write(content));

        policy.MaxAgeDays.Should().Be(30);
        policy.MaxCount.Should().BeNull();
        policy.MaxTotalBytes.Should().BeNull();
        policy.Source.Should().Contain("defaults");
    }

    [Fact]
    public void Absurdly_large_values_are_clamped()
    {
        RetentionPolicy.Load(Write("""{ "Retention": { "MaxAgeDays": 999999999999 } }""")).MaxAgeDays.Should().BeLessThanOrEqualTo(36500);
    }
}
