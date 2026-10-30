// How expensive are single file-system operations on this machine? Used to tell "ProcLens is slow" apart from
// "every file operation here is slow" (for example real-time antivirus scanning each open).
//
//     dotnet run -c Release tools/perf/fsprobe.cs

using System.Diagnostics;

const int n = 5000;
var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-perf-fs-" + Guid.NewGuid().ToString("N"))).FullName;
try
{
    var files = new string[n];
    var body = new string('x', 800);
    for (var i = 0; i < n; i++) { files[i] = Path.Combine(dir, $"f{i}.dmp.enc.json"); File.WriteAllText(files[i], body); }

    Measure("enumerate the directory", () => { var c = Directory.EnumerateFiles(dir).Count(); }, perItem: false);
    Measure("File.Exists", () => { foreach (var f in files) File.Exists(f); });
    Measure("File.GetAttributes", () => { foreach (var f in files) File.GetAttributes(f); });
    Measure("new FileInfo(f).Length", () => { foreach (var f in files) _ = new FileInfo(f).Length; });
    Measure("File.ReadAllText (open + read + close)", () => { foreach (var f in files) File.ReadAllText(f); });
    Measure("open + read 8 bytes + close", () =>
    {
        foreach (var f in files)
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> b = stackalloc byte[8];
            s.ReadAtLeast(b, 8, false);
        }
    });
    Measure("File.Delete", () => { foreach (var f in files) File.Delete(f); });
}
finally { Directory.Delete(dir, true); }

static void Measure(string what, Action action, bool perItem = true)
{
    var watch = Stopwatch.StartNew();
    action();
    watch.Stop();
    Console.WriteLine(perItem
        ? $"  {what,-42} {watch.Elapsed.TotalMilliseconds,8:N0} ms total  = {watch.Elapsed.TotalMilliseconds * 1000 / n,7:N0} us per file"
        : $"  {what,-42} {watch.Elapsed.TotalMilliseconds,8:N0} ms total");
}
