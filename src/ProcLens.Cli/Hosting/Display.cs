using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcLens.Core.Models;
using ProcLens.Core.Reports;

namespace ProcLens.Cli.Hosting;

/// <summary>Formatting shared by the commands. Stateless; nothing here decides what to say, only how it looks.</summary>
public static class Display
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    /// <summary>An unavailable measurement is shown as "n/a", never as zero.</summary>
    public static string Format<T>(Measurement<T> m, Func<T, string> render) where T : struct =>
        m.IsAvailable ? render(m.Value!.Value) : "n/a";

    public static string Bytes(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

    /// <summary>Terminal columns taken by the text (wide East Asian characters take two), so tables stay aligned in Chinese too.</summary>
    public static int Width(string text) => TextWidth.Of(text);

    public static string PadRight(string text, int width) => TextWidth.PadRight(text, width);
    public static string PadLeft(string text, int width) => TextWidth.PadLeft(text, width);

    public static string Seconds(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
