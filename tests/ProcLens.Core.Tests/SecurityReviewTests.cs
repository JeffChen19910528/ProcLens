using FluentAssertions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;
using ProcLens.Core.Security;

namespace ProcLens.Core.Tests;

/// <summary>Regression tests for the findings of the security review (docs/SECURITY_REVIEW.md).</summary>
public class SecurityReviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-secrev-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private bool TrySymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } // needs privilege on Windows
    }

    // ---------- F3: secure delete must never write through a link ----------

    [Fact]
    public void SecureDelete_removes_a_symbolic_link_without_overwriting_what_it_points_to()
    {
        var precious = P("precious.txt");
        File.WriteAllText(precious, "DO NOT TOUCH");
        var link = P("looks-like-a-dump.dmp");
        if (!TrySymlink(link, precious)) return;

        RestrictedFile.SecureDelete(link);

        File.Exists(link).Should().BeFalse("the link itself is removed");
        File.ReadAllText(precious).Should().Be("DO NOT TOUCH", "overwriting must not follow the link to another file");
    }

    // ---------- F2: untrusted files cannot exhaust memory ----------

    [Fact]
    public void Oversized_metadata_is_treated_as_invalid_without_reading_it()
    {
        var dump = P("big.dmp");
        File.WriteAllBytes(dump, [1]);
        using (var f = File.Create(DumpFiles.SidecarPath(dump)))
        {
            f.SetLength(DumpFiles.MaxMetadataBytes + 1);
        }

        DumpFiles.TryReadMetadata(dump).Should().BeNull();
    }

    [Fact]
    public void Oversized_policy_files_are_ignored_and_fall_back_to_the_safe_defaults()
    {
        var path = P("policy.json");
        using (var f = File.Create(path)) f.SetLength(DumpFiles.MaxMetadataBytes + 1);

        AdminDumpPolicy.Load(path).AdminDumpsEnabled.Should().BeFalse();
        RetentionPolicy.Load(path).MaxAgeDays.Should().Be(RetentionPolicy.DefaultMaxAgeDays);
    }

    // ---------- F4: a crafted file cannot demand unbounded key-derivation work ----------

    [Fact]
    public void The_accepted_pbkdf2_iteration_ceiling_is_modest()
    {
        PasswordKekSource.MaxIterations.Should().BeLessThanOrEqualTo(3_000_000);
        PasswordKekSource.MaxIterations.Should().BeGreaterThan(PasswordKekSource.DefaultIterations, "files written today must stay readable");
    }

    // ---------- F5: output paths ----------

    [Theory]
    [InlineData(@"C:\reports\out.txt", true)]
    [InlineData(@"relative\dir\out.html", true)]
    [InlineData(@"C:\reports\a:b.txt", false)]       // NTFS alternate data stream
    [InlineData(@"out.txt:hidden", false)]
    [InlineData(@"C:\reports\CON", false)]            // DOS device names
    [InlineData(@"C:\reports\nul.txt", false)]
    [InlineData(@"C:\reports\Com1", false)]
    [InlineData(@"C:\reports\LPT9.log", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"\\?\GLOBALROOT\Device\x", false)]
    [InlineData("C:\\reports\\bad\u0001name", false)]
    [InlineData(@"C:\reports\console.txt", true)]     // merely starts like a device name
    [InlineData(@"\\server\share\out.txt", true)]
    public void Output_paths_on_windows_reject_streams_devices_and_control_characters(string path, bool accepted)
    {
        OutputPathPolicy.Check(path, windows: true).Should().Match<string?>(r => (r == null) == accepted);
    }

    [Fact]
    public void Unix_output_paths_only_reject_control_characters()
    {
        OutputPathPolicy.Check("/tmp/a:b.txt", windows: false).Should().BeNull("a colon is an ordinary character there");
        OutputPathPolicy.Check("/tmp/bad\u0001", windows: false).Should().NotBeNull();
        OutputPathPolicy.Check("", windows: false).Should().NotBeNull();
    }
}
