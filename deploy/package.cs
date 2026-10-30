// Builds the ProcLens release packages. Run from anywhere inside the repository with the .NET 10 SDK:
//
//     dotnet run deploy/package.cs                                  all platforms, runs the tests first
//     dotnet run deploy/package.cs -- --rids win-x64,linux-x64      only some
//     dotnet run deploy/package.cs -- --skip-tests --version 1.0.1
//
// Output: artifacts/<version>/proclens-<version>-<rid>.zip|.tar.gz and SHA256SUMS.txt.
// Why C# instead of a shell script: it behaves the same on Windows, Linux and macOS, and it can write the Unix
// permission bits (executable) into the tar archives even when it runs on Windows, which tar.exe cannot.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

var options = Options.Parse(args);
if (options is null) return 2;

var root = FindRepositoryRoot();
if (root is null)
{
    Console.Error.WriteLine("Run this from inside the ProcLens repository (ProcLens.sln was not found above the current directory).");
    return 2;
}

var output = Path.GetFullPath(options.Output ?? Path.Combine(root, "artifacts"), root);
var versionDir = Path.Combine(output, options.Version);
var stageRoot = Path.Combine(versionDir, "stage");
Directory.CreateDirectory(versionDir);
if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);

Console.WriteLine($"ProcLens {options.Version}: packaging {string.Join(", ", options.Rids)} into {versionDir}");

if (!options.SkipTests)
{
    Console.WriteLine("== running the test suite (use --skip-tests to skip) ==");
    if (Run("dotnet", ["test", "-c", "Release", "--nologo", "-v", "quiet"], root) != 0)
    {
        Console.Error.WriteLine("Tests failed; no packages were built.");
        return 1;
    }
}

var archives = new List<string>();
foreach (var rid in options.Rids)
{
    var family = Family(rid);
    var name = $"proclens-{options.Version}-{rid}";
    var stage = Path.Combine(stageRoot, name);
    Directory.CreateDirectory(stage);

    Console.WriteLine($"== {rid}: publishing ==");
    var publish = Run("dotnet",
    [
        "publish", Path.Combine("src", "ProcLens.Cli", "ProcLens.Cli.csproj"),
        "-c", "Release", "-r", rid, "--self-contained", "true", "--nologo", "-v", "quiet",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none", "-p:DebugSymbols=false",
        $"-p:Version={options.Version}", $"-p:InformationalVersion={options.Version}",
        "-o", stage,
    ], root);
    if (publish != 0)
    {
        Console.Error.WriteLine($"Publishing {rid} failed.");
        return 1;
    }

    Assemble(stage, root, family, rid, options.Version);

    var archive = family == "windows"
        ? Path.Combine(versionDir, name + ".zip")
        : Path.Combine(versionDir, name + ".tar.gz");
    if (File.Exists(archive)) File.Delete(archive);
    if (family == "windows") ZipFile.CreateFromDirectory(stage, archive, CompressionLevel.SmallestSize, includeBaseDirectory: true);
    else WriteTarGz(stage, name, archive);

    archives.Add(archive);
    Console.WriteLine($"   {Path.GetFileName(archive)}  {new FileInfo(archive).Length / 1024.0 / 1024.0:N1} MB");
}

var sums = Path.Combine(versionDir, "SHA256SUMS.txt");
File.WriteAllText(sums,
    string.Concat(archives.OrderBy(a => a, StringComparer.Ordinal).Select(a =>
        $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a)))}  {Path.GetFileName(a)}\n")),
    new UTF8Encoding(false));
Console.WriteLine($"== checksums written to {sums} ==");
Directory.Delete(stageRoot, recursive: true);

if (!options.SkipSmokeTest)
{
    var hostRid = HostRid();
    var hostArchive = archives.FirstOrDefault(a => Path.GetFileName(a).Contains($"-{hostRid}."));
    if (hostArchive is null)
    {
        Console.WriteLine($"== smoke test skipped: no package for this machine ({hostRid}) was built ==");
    }
    else if (!SmokeTest(hostArchive, options.Version))
    {
        Console.Error.WriteLine("The smoke test of the package failed.");
        return 1;
    }
}

Console.WriteLine("Done. Packages for platforms other than this machine's were built but NOT run.");
return 0;

// ----------------------------------------------------------------------------------------------------------------

static string? FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "ProcLens.sln"))) return dir.FullName;
    return null;
}

static string Family(string rid) =>
    rid.StartsWith("win", StringComparison.Ordinal) ? "windows" : rid.StartsWith("osx", StringComparison.Ordinal) ? "macos" : "linux";

static string HostRid()
{
    var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    return OperatingSystem.IsWindows() ? $"win-{arch}" : OperatingSystem.IsMacOS() ? $"osx-{arch}" : $"linux-{arch}";
}

static int Run(string file, IEnumerable<string> args, string workingDirectory)
{
    var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, UseShellExecute = false };
    foreach (var a in args) info.ArgumentList.Add(a);
    using var process = Process.Start(info)!;
    process.WaitForExit();
    return process.ExitCode;
}

/// <summary>Adds documentation, the installer and a README next to the published executable.</summary>
static void Assemble(string stage, string root, string family, string rid, string version)
{
    var docs = Path.Combine(stage, "docs");
    Directory.CreateDirectory(docs);
    foreach (var file in Directory.GetFiles(Path.Combine(root, "docs"), "*.md")) File.Copy(file, Path.Combine(docs, Path.GetFileName(file)));

    foreach (var file in Directory.GetFiles(Path.Combine(root, "deploy", family)).Where(f => !f.EndsWith(".md")))
        File.Copy(file, Path.Combine(stage, Path.GetFileName(file)));

    var license = Path.Combine(root, "LICENSE");
    if (File.Exists(license)) File.Copy(license, Path.Combine(stage, "LICENSE"));

    File.WriteAllText(Path.Combine(stage, "README.txt"), Readme(family, rid, version, File.Exists(license)), new UTF8Encoding(false));
}

static string Readme(string family, string rid, string version, bool hasLicense)
{
    var exe = family == "windows" ? "proclens.exe" : "proclens";
    var install = family == "windows"
        ? "  powershell -ExecutionPolicy Bypass -File .\\install.ps1          (installs for your user, adds it to your PATH)\n  powershell -File .\\install.ps1 -Uninstall"
        : "  ./install.sh                  (installs under ~/.local, or /usr/local when run as root)\n  ./install.sh --prefix /opt/proclens\n  ./install.sh --uninstall";

    var notes = new StringBuilder();
    notes.AppendLine("Status of this package");
    notes.AppendLine("  Built from the same source as every other platform. Tested on: Windows 11 x64, Ubuntu 24.04 x64 (under WSL2).");
    if (family == "macos")
    {
        notes.AppendLine("  THIS PACKAGE HAS NOT BEEN RUN ON A MAC. macOS support is implemented and compiled but unverified; see docs/PLATFORM_SUPPORT.md.");
        notes.AppendLine("  It is not code-signed or notarised, so Gatekeeper blocks it until you remove the quarantine flag (see below).");
    }
    else if (rid.EndsWith("arm64", StringComparison.Ordinal))
    {
        notes.AppendLine("  THIS ARM64 PACKAGE HAS NOT BEEN RUN ON ARM64 HARDWARE.");
    }
    if (family == "windows") notes.AppendLine("  The executable is not code-signed; SmartScreen may warn. Verify the checksum first.");
    if (!hasLicense) notes.AppendLine("  No license file is included in this build.");

    return $"""
        ProcLens {version} ({rid})
        Cross-platform process diagnostics toolkit.

        This is a self-contained package: it needs no .NET runtime. It contains
          {exe}        the command-line tool
          docs\          documentation (CLI_USAGE.md first)
          install script

        {notes.ToString().TrimEnd()}

        1. Verify the download
           Compare the SHA-256 of the archive with SHA256SUMS.txt from the release.
             Windows:  Get-FileHash .\proclens-{version}-{rid}.{(family == "windows" ? "zip" : "tar.gz")} -Algorithm SHA256
             Unix:     sha256sum -c SHA256SUMS.txt      (or: shasum -a 256 <file>)

        2. Install (or just run {exe} from here)
        {install}

        3. Try it
             proclens system capabilities
             proclens process list --sort memory
             proclens report generate --pid <pid> --format html --output report.html

        Dumps, encryption, the audit log, policy and retention are described in docs/CLI_USAGE.md and docs/SECURITY.md.
        Uninstalling never deletes your audit log or your dump master key; see docs/CLI_USAGE.md ("Files, environment variables and policy").
        """.Replace("\r\n", "\n");
}

static void WriteTarGz(string stage, string rootName, string archivePath)
{
    using var file = File.Create(archivePath);
    using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

    var time = DateTimeOffset.UtcNow;
    const UnixFileMode directoryMode = (UnixFileMode)0b111_101_101;   // rwxr-xr-x
    const UnixFileMode executableMode = (UnixFileMode)0b111_101_101;
    const UnixFileMode fileMode = (UnixFileMode)0b110_100_100;        // rw-r--r--

    void Add(TarEntry entry, UnixFileMode mode)
    {
        entry.Mode = mode;
        entry.Uid = 0;
        entry.Gid = 0;
        entry.ModificationTime = time;
        tar.WriteEntry(entry);
    }

    Add(new PaxTarEntry(TarEntryType.Directory, rootName + "/"), directoryMode);
    foreach (var dir in Directory.GetDirectories(stage, "*", SearchOption.AllDirectories).OrderBy(d => d, StringComparer.Ordinal))
        Add(new PaxTarEntry(TarEntryType.Directory, EntryName(dir) + "/"), directoryMode);

    foreach (var path in Directory.GetFiles(stage, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
    {
        var name = Path.GetFileName(path);
        var executable = name == "proclens" || name.EndsWith(".sh", StringComparison.Ordinal);
        using var data = File.OpenRead(path);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, EntryName(path)) { DataStream = data };
        Add(entry, executable ? executableMode : fileMode);
    }

    string EntryName(string path) => rootName + "/" + Path.GetRelativePath(stage, path).Replace('\\', '/');
}

/// <summary>Extracts the package the way a user would and runs it.</summary>
static bool SmokeTest(string archive, string version)
{
    Console.WriteLine($"== smoke test of {Path.GetFileName(archive)} ==");
    var temp = Path.Combine(Path.GetTempPath(), "proclens-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        if (archive.EndsWith(".zip", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(archive, temp);
        else
        {
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, temp, overwriteFiles: false);
        }

        var packageDir = Directory.GetDirectories(temp).Single();
        var exe = Path.Combine(packageDir, OperatingSystem.IsWindows() ? "proclens.exe" : "proclens");
        var self = Environment.ProcessId.ToString();

        // Redirect everything ProcLens writes so the test leaves no trace in the user's profile.
        var environment = new Dictionary<string, string>
        {
            ["PROCLENS_AUDIT_LOG"] = Path.Combine(temp, "audit.jsonl"),
            ["PROCLENS_KEY_DIR"] = Path.Combine(temp, "keys"),
            ["PROCLENS_POLICY"] = Path.Combine(temp, "policy.json"),
        };

        var ok = true;
        ok &= Check(exe, ["--version"], environment, output => output.Trim() == version, "prints its version");
        ok &= Check(exe, ["system", "capabilities"], environment, output => output.Contains("ProcessInspection: Supported"), "reports capabilities");
        ok &= Check(exe, ["process", "inspect", "--pid", self], environment, output => output.Contains($"PID:            {self}"), "inspects a process");
        ok &= Check(exe, ["report", "generate", "--pid", self, "--format", "json", "--samples", "1"], environment,
            output => output.Contains("\"SchemaVersion\": 1"), "generates a report");
        ok &= File.Exists(Path.Combine(packageDir, "docs", "CLI_USAGE.md"));
        ok &= File.Exists(Path.Combine(packageDir, "README.txt"));
        if (!OperatingSystem.IsWindows())
            ok &= (File.GetUnixFileMode(exe) & UnixFileMode.UserExecute) != 0;
        Console.WriteLine(ok ? "   smoke test passed" : "   SMOKE TEST FAILED");
        return ok;
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

static bool Check(string exe, string[] args, Dictionary<string, string> environment, Func<string, bool> accept, string what)
{
    var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in args) info.ArgumentList.Add(a);
    foreach (var (k, v) in environment) info.Environment[k] = v;
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    var finished = process.WaitForExit(TimeSpan.FromSeconds(60));
    var passed = finished && process.ExitCode == 0 && accept(stdout.Result);
    Console.WriteLine($"   [{(passed ? "ok" : "FAIL")}] {what}{(passed ? "" : $" (exit {(finished ? process.ExitCode : -1)}) {stderr.Result.Trim()}")}");
    return passed;
}

sealed record Options(string Version, string[] Rids, string? Output, bool SkipTests, bool SkipSmokeTest)
{
    public static readonly string[] AllRids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    public static Options? Parse(string[] args)
    {
        var version = "1.0.0";
        string[] rids = AllRids;
        string? output = null;
        bool skipTests = false, skipSmoke = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version" when i + 1 < args.Length: version = args[++i]; break;
                case "--rids" when i + 1 < args.Length: rids = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--output" when i + 1 < args.Length: output = args[++i]; break;
                case "--skip-tests": skipTests = true; break;
                case "--no-smoke-test": skipSmoke = true; break;
                default:
                    Console.Error.WriteLine("Usage: dotnet run deploy/package.cs -- [--version 1.0.0] [--rids win-x64,linux-x64,...] [--output dir] [--skip-tests] [--no-smoke-test]");
                    Console.Error.WriteLine($"Known runtime ids: {string.Join(", ", AllRids)}");
                    return null;
            }
        }

        var unknown = rids.Where(r => !AllRids.Contains(r)).ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"Unknown runtime id(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", AllRids)}");
            return null;
        }
        return new Options(version, rids, output, skipTests, skipSmoke);
    }
}
