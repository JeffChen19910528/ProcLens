// End-to-end measurements: runs the real proclens executable and records wall time, CPU time and peak memory.
//
//     dotnet run -c Release tools/perf/e2e.cs -- --exe path\to\proclens.exe [--only startup,latency,monitor,audit,dump]
//                                               [--baseline path\to\older\proclens.exe] [--dump-mb 256,1024]
//
// --baseline is used by the audit scenario to compare against an earlier build. Everything ProcLens writes is
// redirected to a temporary folder. Windows only for the dump scenario (it is the only platform with dumps).

#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../src/ProcLens.Core

using System.Diagnostics;
using System.Runtime.InteropServices;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;

var options = args.Chunk(2).Where(c => c.Length == 2).ToDictionary(c => c[0], c => c[1]);
var exe = options.GetValueOrDefault("--exe");
if (exe is null || !File.Exists(exe))
{
    Console.Error.WriteLine("Usage: dotnet run -c Release tools/perf/e2e.cs -- --exe <proclens executable> [--only a,b] [--baseline <older exe>] [--dump-mb 256,1024]");
    return 2;
}
var only = (options.GetValueOrDefault("--only") ?? "startup,latency,monitor,audit,dump").Split(',');
var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-perf-e2e-" + Guid.NewGuid().ToString("N"))).FullName;
var environment = new Dictionary<string, string>
{
    ["PROCLENS_AUDIT_LOG"] = Path.Combine(work, "audit", "audit.jsonl"),
    ["PROCLENS_KEY_DIR"] = Path.Combine(work, "keys"),
    ["PROCLENS_POLICY"] = Path.Combine(work, "none.json"),
};

Console.WriteLine($"{RuntimeInformation.OSDescription} | {Environment.ProcessorCount} logical processors | {exe} ({new FileInfo(exe).Length / 1048576.0:N0} MB)");
Console.WriteLine();
try
{
    if (only.Contains("startup")) Startup();
    if (only.Contains("latency")) Latency();
    if (only.Contains("monitor")) await Monitor();
    if (only.Contains("audit")) Audit(options.GetValueOrDefault("--baseline"));
    if (only.Contains("dump") && OperatingSystem.IsWindows())
        await DumpScenario((options.GetValueOrDefault("--dump-mb") ?? "256,1024").Split(',').Select(int.Parse).ToArray());
}
finally { try { Directory.Delete(work, true); } catch (IOException) { } }
return 0;

// ------------------------------------------------------------------------------------------------------------

Measurement RunExe(string program, params string[] arguments)
{
    var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in arguments) info.ArgumentList.Add(a);
    foreach (var (k, v) in environment) info.Environment[k] = v;

    var watch = Stopwatch.StartNew();
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    long peak = 0;
    while (!process.WaitForExit(15))
    {
        try { process.Refresh(); peak = Math.Max(peak, process.PeakWorkingSet64); } catch (InvalidOperationException) { }
    }
    process.WaitForExit();
    watch.Stop();
    return new Measurement(watch.Elapsed, process.TotalProcessorTime, peak, process.ExitCode, stdout.Result, stderr.Result);
}

static string Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return $"{sorted[sorted.Length / 2],7:N0} ms (min {sorted[0]:N0}, max {sorted[^1]:N0})";
}

void Startup()
{
    Console.WriteLine("=== Start-up: proclens --version ===");
    var runs = Enumerable.Range(0, 25).Select(_ => RunExe(exe, "--version")).ToList();
    Console.WriteLine($"  first run:  {runs[0].Wall.TotalMilliseconds,6:N0} ms");
    Console.WriteLine($"  next 24:    median {Median(runs.Skip(1).Select(r => r.Wall.TotalMilliseconds))}, CPU {runs.Skip(1).Average(r => r.Cpu.TotalMilliseconds):N0} ms, peak memory {runs.Max(r => r.PeakBytes) / 1048576.0:N0} MB");
    Console.WriteLine();
}

void Latency()
{
    Console.WriteLine("=== Command latency (median of 7 runs, includes process start-up) ===");
    var explorer = Process.GetProcessesByName(OperatingSystem.IsWindows() ? "explorer" : "systemd").FirstOrDefault()?.Id ?? Environment.ProcessId;
    var self = Environment.ProcessId.ToString();
    var cases = new (string Name, string[] Args)[]
    {
        ("system capabilities", ["system", "capabilities"]),
        ("process inspect (one process)", ["process", "inspect", "--pid", self]),
        ("process list", ["process", "list"]),
        ("process list --sort memory", ["process", "list", "--sort", "memory"]),
        ("process list --sort cpu  (samples twice, 1 s apart)", ["process", "list", "--sort", "cpu"]),
        ($"process threads (pid {explorer})", ["process", "threads", "--pid", explorer.ToString()]),
        ($"process modules (pid {explorer})", ["process", "modules", "--pid", explorer.ToString()]),
        ("report txt  (1 sample, no wait)", ["report", "generate", "--pid", explorer.ToString(), "--samples", "1"]),
        ("report html (1 sample, no wait)", ["report", "generate", "--pid", explorer.ToString(), "--samples", "1", "--format", "html"]),
        ("report json (1 sample, no wait)", ["report", "generate", "--pid", explorer.ToString(), "--samples", "1", "--format", "json"]),
    };
    foreach (var (name, arguments) in cases)
    {
        var runs = Enumerable.Range(0, name.Contains("sort cpu") ? 3 : 7).Select(_ => RunExe(exe, arguments)).ToList();
        Console.WriteLine($"  {name,-52} {Median(runs.Select(r => r.Wall.TotalMilliseconds))}  peak {runs.Max(r => r.PeakBytes) / 1048576.0,4:N0} MB  exit {runs[0].ExitCode}");
    }
    var processCount = Process.GetProcesses().Length;
    Console.WriteLine($"  ({processCount} processes were running)");
    Console.WriteLine();
}

async Task Monitor()
{
    Console.WriteLine("=== Monitoring overhead: 'process monitor --interval 0.1 --count 100' of a busy process ===");
    using var busy = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "bash")
    {
        ArgumentList = { OperatingSystem.IsWindows() ? "-NoProfile" : "-c", OperatingSystem.IsWindows() ? "-Command" : "while :; do :; done", "while($true){}" },
        UseShellExecute = false, CreateNoWindow = true,
    })!;
    try
    {
        await Task.Delay(1500);
        var run = RunExe(exe, "process", "monitor", "--pid", busy.Id.ToString(), "--interval", "0.1", "--count", "100");
        var seconds = run.Wall.TotalSeconds;
        Console.WriteLine($"  100 samples in {seconds:N1} s ({seconds / 100 * 1000:N0} ms per sample, target 100 ms)");
        Console.WriteLine($"  ProcLens used {run.Cpu.TotalMilliseconds:N0} ms of CPU in total = {run.Cpu.TotalSeconds / seconds * 100:N1}% of one core; peak memory {run.PeakBytes / 1048576.0:N0} MB");
    }
    finally { busy.Kill(); }
    Console.WriteLine();
}

void Audit(string? baseline)
{
    Console.WriteLine("=== Audit log CLI at 100,000 records (81 MB file) ===");
    var path = environment["PROCLENS_AUDIT_LOG"];
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var previous = AuditChain.Genesis;
    using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 1 << 16))
        for (var i = 0; i < 100_000; i++)
        {
            var record = AuditChain.Seal(new AuditRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "DOMAIN\\operator", false, "HOST", "dump.create", 1000 + i % 5000,
                "someprocess", DateTimeOffset.UtcNow, "DOMAIN\\operator", "Heap", "C:\\dumps\\someprocess_1234_heap_20260101T000000Z_abcdef12.dmp.enc",
                new AuditAuthorization(true, "owner", "The operator owns the target process."), AuditOutcome.Succeeded, null, new string('a', 64), true, null), previous);
            w.Write(AuditChain.ToLine(record)); w.Write('\n');
            previous = record.Hash;
        }
    Console.WriteLine($"  log file: {new FileInfo(path).Length / 1048576.0:N0} MB");

    foreach (var (label, program) in new[] { ("this build", exe), ("baseline ", baseline) })
    {
        if (program is null) continue;
        foreach (var arguments in new[] { new[] { "audit", "verify" }, ["audit", "show", "--last", "5"] })
        {
            var runs = Enumerable.Range(0, 3).Select(_ => RunExe(program, arguments)).ToList();
            Console.WriteLine($"  {label} {string.Join(' ', arguments),-22} {Median(runs.Select(r => r.Wall.TotalMilliseconds))}   peak memory {runs.Max(r => r.PeakBytes) / 1048576.0,6:N0} MB   exit {runs[0].ExitCode}");
        }
    }
    Console.WriteLine();
}

async Task DumpScenario(int[] sizesMb)
{
    Console.WriteLine("=== Dump creation (Windows). Target: a PowerShell process holding the stated amount of random data ===");
    var dumps = Path.Combine(work, "dumps");
    var drive = new DriveInfo(Path.GetPathRoot(work)!);

    // Reference: how fast can this disk be written at all?
    var probe = Path.Combine(work, "diskprobe.bin");
    var block = new byte[1 << 20];
    Random.Shared.NextBytes(block);
    var watch = Stopwatch.StartNew();
    using (var f = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
        for (var i = 0; i < 512; i++) f.Write(block);
    Console.WriteLine($"  reference: sequential write of 512 MB to {drive.Name} (write-through): {512 / watch.Elapsed.TotalSeconds,6:N0} MB/s");
    File.Delete(probe);
    Console.WriteLine();

    foreach (var mb in sizesMb)
    {
        using var target = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-Command", $"$b = New-Object byte[] {mb}MB; (New-Object Random 1).NextBytes($b); Start-Sleep 600" },
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            // wait until it actually holds the memory
            for (var i = 0; i < 120 && target.PrivateMemorySize64 < mb * 1048576L * 0.9; i++) { await Task.Delay(500); target.Refresh(); }
            Console.WriteLine($"  --- target holds {target.PrivateMemorySize64 / 1048576.0:N0} MB (private) ---");
            Console.WriteLine($"  {"type",-6} {"protection",-11} {"wall",8} {"dump size",11} {"rate",10} {"ProcLens CPU",13} {"ProcLens peak mem",18}");

            foreach (var type in new[] { "mini", "heap", "full" })
                foreach (var encrypt in new[] { false, true })
                {
                    if (type != "mini" && drive.AvailableFreeSpace < 4L * mb * 1048576) continue;
                    var arguments = new List<string> { "dump", "create", "--pid", target.Id.ToString(), "--type", type, "--output", dumps, "--json" };
                    if (!encrypt) arguments.Add("--no-encrypt");
                    var run = RunExe(exe, [.. arguments]);
                    var file = Directory.Exists(dumps) ? Directory.GetFiles(dumps).Where(f => !f.EndsWith(".json")).OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault() : null;
                    var size = file is null ? 0 : new FileInfo(file).Length;
                    Console.WriteLine($"  {type,-6} {(encrypt ? "encrypted" : "plain"),-11} {run.Wall.TotalSeconds,7:N1}s {size / 1048576.0,8:N0} MB {size / 1048576.0 / run.Wall.TotalSeconds,6:N0} MB/s {run.Cpu.TotalSeconds,11:N1} s {run.PeakBytes / 1048576.0,14:N0} MB   exit {run.ExitCode}");
                    if (Directory.Exists(dumps)) Directory.Delete(dumps, true);
                }
        }
        finally { target.Kill(); await Task.Delay(1500); }
        Console.WriteLine();
    }
}

readonly record struct Measurement(TimeSpan Wall, TimeSpan Cpu, long PeakBytes, int ExitCode, string Out, string Err);
