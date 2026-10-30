// Where does the time of a retention scan go? Times each step the scan performs, over the same kind of files.
//
//     dotnet run -c Release tools/perf/retprobe.cs

#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../src/ProcLens.Core
#:project ../../src/ProcLens.Application

using System.Diagnostics;
using ProcLens.Application.Dumps;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

const int n = 2000;
var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-perf-rp-" + Guid.NewGuid().ToString("N"))).FullName;
try
{
    var dumps = new string[n];
    for (var i = 0; i < n; i++)
    {
        var path = dumps[i] = Path.Combine(dir, $"p_{i}_mini_20260101T000000Z_{i:x8}.dmp.enc");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var created = DateTimeOffset.UtcNow.AddDays(-(i % 90));
        File.WriteAllText(DumpFiles.SidecarPath(path), DumpFiles.SerializeMetadata(new DumpMetadata(
            Guid.NewGuid(), i, "p", created.AddMinutes(-1), DumpType.Mini, path, 4, created, "OS", "1",
            CollectionStatus.Succeeded, null, "a", "b", DumpCipher.SchemeName)));
    }
    var sidecars = dumps.Select(DumpFiles.SidecarPath).ToArray();
    Console.WriteLine($"{n} dumps, sidecar size {new FileInfo(sidecars[0]).Length} bytes\n");

    Step("File.ReadAllText(sidecar)", () => { foreach (var s in sidecars) File.ReadAllText(s); });
    Step("File.ReadAllText(sidecar) again (now cached/scanned)", () => { foreach (var s in sidecars) File.ReadAllText(s); });
    Step("DumpFiles.TryReadMetadata(dump)", () => { foreach (var d in dumps) DumpFiles.TryReadMetadata(d); });
    Step("DumpCipher.IsEncrypted(dump)  [opens the dump]", () => { foreach (var d in dumps) DumpCipher.IsEncrypted(d); });

    var metas = dumps.Select(d => DumpFiles.TryReadMetadata(d)!).ToArray();
    var managed = dumps.Select((d, i) => new ManagedDump(d, sidecars[i], metas[i], 100)).ToList();
    Step("RetentionPlanner.Plan (pure logic, no I/O)", () => RetentionPlanner.Plan(managed, new RetentionPolicy(MaxAgeDays: 30, MaxCount: 100, MaxTotalBytes: 10_000), DateTimeOffset.UtcNow));

    var service = new DumpRetentionService(new Audit(), new Op());
    Step("DumpRetentionService.RunAsync (everything)", () => service.RunAsync(dir, new RetentionPolicy(MaxAgeDays: 30), delete: false).GetAwaiter().GetResult());
}
finally { Directory.Delete(dir, true); }

void Step(string what, Action action)
{
    var watch = Stopwatch.StartNew();
    action();
    Console.WriteLine($"  {what,-58} {watch.Elapsed.TotalMilliseconds,8:N0} ms  = {watch.Elapsed.TotalMilliseconds * 1000 / n,7:N0} us per dump");
}

sealed class Audit : IAuditLog
{
    public Task EnsureAvailableAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task AppendAsync(AuditRecord record, CancellationToken ct = default) => Task.CompletedTask;
}

sealed class Op : IOperatorContext
{
    public string Identity => "bench";
    public bool IsElevated => false;
    public string Machine => "bench";
}
