using System.Globalization;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Core.Reports;

namespace ProcLens.Infrastructure.Reports;

public enum ViewKind
{
    Ok,
    Muted,
    Warn,
    Bad,
}

public sealed record ViewTable(string Caption, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <param name="Id">The English section key. Anchors and identifiers use it, so they do not change with the display language.</param>
public sealed record ViewSection(
    string Id,
    string Name,
    string StatusLabel,
    ViewKind Kind,
    string? Note,
    IReadOnlyList<KeyValuePair<string, string>> Facts,
    IReadOnlyList<ViewTable> Tables);

public sealed record ReportView(string Title, string Subtitle, string Heading, IReadOnlyList<ViewSection> Sections);

/// <summary>
/// The single description of what a report says, shared by the text and HTML renderers so they cannot drift apart.
/// Every string that originates from a process (names, paths, owners, thread names, reasons) passes through
/// <see cref="Clean"/> here, so renderers never see control or direction-overriding characters. Every label and
/// sentence the report adds around the data passes through the <see cref="ITextLocalizer"/>.
/// </summary>
public static class ReportViewBuilder
{
    public static ReportView Build(DiagnosticReport r) => Build(r, EnglishText.Instance);

    public static ReportView Build(DiagnosticReport r, ITextLocalizer text) => new Composer(text).Build(r);

    /// <summary>Makes untrusted text safe to place in any output: no control characters, line breaks or direction overrides.</summary>
    public static string Clean(string? text)
    {
        if (text is null) return "";
        var chars = text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c);
        return new string(chars.ToArray()).Trim();
    }

    public static string Label(SectionStatus status) => Label(status, EnglishText.Instance);

    public static string Label(SectionStatus status, ITextLocalizer text) => status switch
    {
        SectionStatus.Available => text.Text("Available"),
        SectionStatus.NotSupported => text.Text("Not supported"),
        SectionStatus.AccessDenied => text.Text("Access denied"),
        SectionStatus.ProcessNotFound => text.Text("Process not found"),
        SectionStatus.ArchitectureMismatch => text.Text("Architecture mismatch"),
        SectionStatus.NotCollected => text.Text("Not collected"),
        _ => text.Text("Failed"),
    };

    private sealed class Composer(ITextLocalizer t)
    {
        public ReportView Build(DiagnosticReport r)
        {
            var sections = new List<ViewSection>
            {
                SystemSection(r),
                ProcessSection(r),
                CpuSection(r),
                MemorySection(r),
                ThreadsSection(r),
                ModulesSection(r),
                DumpsSection(r),
                WarningsSection(r),
                ErrorsSection(r),
            };

            var subject = r.Process.Data is { } p ? t.Text("{0} (PID {1})", Clean(p.ProcessName), p.ProcessId) : t.Text("unavailable process");
            return new ReportView(
                t.Text("ProcLens report - {0}", subject),
                t.Text("Generated {0} by ProcLens {1} (schema {2})", $"{r.Header.GeneratedAt:yyyy-MM-dd HH:mm:ss}Z", Clean(r.Header.ApplicationVersion), r.Header.SchemaVersion),
                t.Text("ProcLens Diagnostic Report"),
                sections);
        }

        // ---------- helpers ----------

        private static ViewKind KindOf(SectionStatus status) => status switch
        {
            SectionStatus.Available => ViewKind.Ok,
            SectionStatus.NotSupported or SectionStatus.NotCollected => ViewKind.Muted,
            SectionStatus.AccessDenied => ViewKind.Warn,
            _ => ViewKind.Bad,
        };

        /// <summary>A reason that came from a lower layer: cleaned, then translated if the catalog knows the sentence.</summary>
        private string Reason(string? reason) => t.Localize(Clean(reason));

        /// <summary>An unavailable measurement is shown as "n/a" with its reason; a real zero is shown as 0.</summary>
        private string Value<T>(Measurement<T> m, Func<T, string> format) where T : struct =>
            m.IsAvailable ? format(m.Value!.Value) : t.Text("n/a ({0})", Reason(m.UnavailableReason ?? "not available"));

        private string Bytes(long bytes) => bytes < 1024 ? t.Text("{0} B", bytes) : t.Text("{0:N1} MB ({1:N0} bytes)", bytes / 1024.0 / 1024.0, bytes);
        private static string Percent(double v) => $"{v:F1}%";
        private static string Duration(TimeSpan v) => v.ToString(v.TotalHours >= 1 ? @"d\.hh\:mm\:ss\.fff" : @"mm\:ss\.fff", CultureInfo.InvariantCulture);
        private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

        private KeyValuePair<string, string> Fact(string key, string value) => new(t.Text(key), value);

        private ViewSection Missing<T>(string id, Section<T> section) where T : class => new(
            id, t.Text(id), Label(section.Status, t), KindOf(section.Status), Reason(section.Reason), [], []);

        private ViewSection Present(string id, string? note, IReadOnlyList<KeyValuePair<string, string>> facts, params ViewTable[] tables) =>
            new(id, t.Text(id), Label(SectionStatus.Available, t), ViewKind.Ok, note is null ? null : t.Text(note), facts, tables);

        private string[] Columns(params string[] english) => english.Select(c => t.Text(c)).ToArray();

        // ---------- sections ----------

        private ViewSection SystemSection(DiagnosticReport r)
        {
            var s = r.System;
            return Present("System", null,
            [
                Fact("Platform", Clean(s.Platform)),
                Fact("Operating system", Clean(s.OperatingSystem)),
                Fact("Architecture", Clean(s.Architecture)),
                Fact("Logical processors", Int(s.LogicalProcessors)),
                Fact("Runtime", Clean(s.Runtime)),
                Fact("Machine", Clean(s.Machine)),
            ],
            new ViewTable(t.Text("Capabilities of this platform"), Columns("Capability", "Status", "Reason"),
                s.Capabilities.Select(c => (IReadOnlyList<string>)[c.Capability.ToString(),
                    c.Status == SupportStatus.Supported ? t.Text("Supported") : t.Text("Not supported"), Reason(c.Reason)]).ToList()));
        }

        private ViewSection ProcessSection(DiagnosticReport r)
        {
            if (r.Process.Data is not { } p) return Missing("Process", r.Process);

            return Present("Process", null,
            [
                Fact("Process id", Int(p.ProcessId)),
                Fact("Name", Clean(p.ProcessName)),
                Fact("Executable", p.ExecutablePath is null ? t.Text("n/a (not readable)") : Clean(p.ExecutablePath)),
                Fact("Parent process id", p.ParentProcessId?.ToString(CultureInfo.InvariantCulture) ?? t.Text("n/a (not reported)")),
                Fact("Owner", p.Owner is null ? t.Text("n/a (not readable)") : Clean(p.Owner)),
                Fact("Started", p.StartTime is { } started ? $"{started.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z" : t.Text("n/a (not readable)")),
                Fact("Access to this process", p.AccessStatus.ToString()),
                Fact("Cumulative CPU time", Value(p.CpuTime, Duration)),
                Fact("Working set", Value(p.MemoryUsage, Bytes)),
                Fact("Thread count", Value(p.ThreadCount, Int)),
            ]);
        }

        private ViewSection CpuSection(DiagnosticReport r)
        {
            if (r.Cpu.Data is not { } c) return Missing("Cpu", r.Cpu);

            return Present("Cpu", null,
            [
                Fact("Samples", Int(c.Samples)),
                Fact("Sampling interval", t.Text("{0} s", $"{c.Interval.TotalSeconds:0.##}")),
                Fact("Logical processors", Int(c.LogicalProcessors)),
                Fact("Cumulative CPU time", Value(c.CpuTime, Duration)),
                Fact("Average, % of whole machine", Value(c.AveragePercentOfMachine, Percent)),
                Fact("Peak, % of whole machine", Value(c.PeakPercentOfMachine, Percent)),
                Fact("Average, % of one core", Value(c.AveragePercentOfOneCore, Percent)),
            ]);
        }

        private ViewSection MemorySection(DiagnosticReport r)
        {
            if (r.Memory.Data is not { } m) return Missing("Memory", r.Memory);

            return Present("Memory", null,
            [
                Fact("Working set", Value(m.WorkingSet, Bytes)),
                Fact("Private memory", Value(m.PrivateMemory, Bytes)),
                Fact("Virtual memory", Value(m.VirtualMemory, Bytes)),
                Fact("Peak working set", Value(m.PeakWorkingSet, Bytes)),
            ]);
        }

        private ViewSection ThreadsSection(DiagnosticReport r)
        {
            if (r.Threads.Data is not { } threads) return Missing("Threads", r.Threads);

            var states = threads.ByState.Count == 0 ? t.Text("none") : string.Join(", ", threads.ByState.Select(kv => $"{kv.Key}: {kv.Value}"));
            return Present("Threads", null,
            [
                Fact("Count", Int(threads.Count)),
                Fact("By state", states),
            ],
            new ViewTable(t.Text("Threads"), Columns("Thread id", "State", "CPU time", "Name / detail"),
                threads.Threads.Select(x => (IReadOnlyList<string>)[x.ThreadId.ToString(CultureInfo.InvariantCulture), x.State.ToString(),
                    Value(x.CpuTime, Duration), Clean(string.Join("  ", new[] { x.Name, x.StateDetail }.Where(s => !string.IsNullOrEmpty(s))))]).ToList()));
        }

        private ViewSection ModulesSection(DiagnosticReport r)
        {
            if (r.Modules.Data is not { } modules) return Missing("Modules", r.Modules);

            return Present("Modules", null,
            [
                Fact("Count", Int(modules.Count)),
            ],
            new ViewTable(t.Text("Loaded modules"), Columns("Base address", "Size", "Name", "Path"),
                modules.Select(m => (IReadOnlyList<string>)[Value(m.BaseAddress, a => $"0x{a:X}"), Value(m.Size, Bytes), Clean(m.Name),
                    m.Path is null ? "n/a" : Clean(m.Path)]).ToList()));
        }

        private ViewSection DumpsSection(DiagnosticReport r)
        {
            if (r.Dumps.Data is not { } dumps) return Missing("Dumps", r.Dumps);

            return Present("Dumps", dumps.Count == 0 ? "No dumps of this process were found in the directory." : null,
            [
                Fact("Count", Int(dumps.Count)),
            ],
            new ViewTable(t.Text("Dumps of this process"), Columns("Created", "Type", "Size", "Protection", "File", "SHA256 (stored file)"),
                dumps.Select(d => (IReadOnlyList<string>)[$"{d.CreationTime.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z", d.DumpType.ToString(), Bytes(d.FileSize),
                    d.EncryptionScheme is null ? t.Text("NOT encrypted") : t.Text("encrypted"), Clean(Path.GetFileName(d.DumpFilePath)), d.Sha256]).ToList()));
        }

        private ViewSection WarningsSection(DiagnosticReport r) => r.Warnings.Count == 0
            ? Present("Warnings", "No warnings.", [])
            : new ViewSection("Warnings", t.Text("Warnings"), t.Text("{0} warning(s)", r.Warnings.Count), ViewKind.Warn, null, [],
                [new ViewTable(t.Text("Diagnostic warnings"), Columns("Code", "Message"), r.Warnings.Select(w => (IReadOnlyList<string>)[w.Code, Reason(w.Message)]).ToList())]);

        private ViewSection ErrorsSection(DiagnosticReport r) => r.Errors.Count == 0
            ? Present("Errors", "No collection errors: every requested section was collected, or is marked above as not supported or not collected.", [])
            : new ViewSection("Errors", t.Text("Errors"), t.Text("{0} collection error(s)", r.Errors.Count), ViewKind.Bad, null, [],
                [new ViewTable(t.Text("Data that could not be collected"), Columns("Section", "Status", "Message"),
                    r.Errors.Select(e => (IReadOnlyList<string>)[t.Text(e.Section), Label(e.Status, t), Reason(e.Message)]).ToList())]);
    }
}
