using System.Runtime.Versioning;
using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Platform.Windows;

namespace ProcLens.Windows.Tests;

[SupportedOSPlatform("windows")]
public class SecurityReviewWindowsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-srw-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // ---------- the policy-file trust check against real ACLs ----------

    [SkippableOnNonWindows]
    public void Files_the_operating_system_itself_protects_are_trusted()
    {
        // Positive controls: owned by SYSTEM/TrustedInstaller, writable only by Administrators, readable by Users.
        foreach (var path in new[]
        {
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\drivers\etc\hosts"),
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\win.ini"),
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
        })
        {
            if (!File.Exists(path)) continue;
            PolicyFileTrust.WhyUntrusted(path).Should().BeNull($"{path} is protected by Windows");
        }
    }

    [SkippableOnNonWindows]
    public void A_file_the_current_user_created_is_not_trusted_wherever_it_lives()
    {
        var own = Path.Combine(_dir, "policy.json");
        File.WriteAllText(own, "{}");

        PolicyFileTrust.WhyUntrusted(own).Should().Contain("owned by");
    }

    [SkippableOnNonWindows]
    public void A_policy_planted_in_a_world_creatable_folder_is_ignored_end_to_end()
    {
        // C:\Users\Public is writable by every user, like C:\ProgramData: the same class of location an attacker would use.
        var folder = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "proclens-planted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "policy.json");
            File.WriteAllText(path, """{ "AdminDumpsEnabled": true, "RequireReason": false, "Retention": { "MaxAgeDays": 0, "MaxCount": 0 } }""");

            var policy = MachinePolicy.Load(path, overridden: false);

            policy.IgnoredBecause.Should().NotBeNull();
            policy.Admin.AdminDumpsEnabled.Should().BeFalse();
            policy.Retention.MaxAgeDays.Should().Be(RetentionPolicy.DefaultMaxAgeDays);
            policy.Retention.MaxCount.Should().BeNull();
        }
        finally { Directory.Delete(folder, true); }
    }

    // ---------- output paths ----------

    [SkippableOnNonWindows]
    public async Task Dump_output_directories_that_name_a_data_stream_or_device_are_refused_before_anything_happens()
    {
        using var target = new TargetProcess();

        foreach (var bad in new[] { _dir + @"\folder:stream", @"\\.\C:\dumps", "CON", Path.Combine(_dir, "NUL") })
        {
            var result = await new WindowsProcessDumpProvider().CreateDumpAsync(target.Request(bad));

            result.Status.Should().Be(DumpStatus.InvalidRequest, bad);
        }
        Directory.GetFileSystemEntries(_dir).Should().BeEmpty();
    }
}
