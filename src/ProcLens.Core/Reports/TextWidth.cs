namespace ProcLens.Core.Reports;

/// <summary>Terminal columns taken by text. East Asian wide characters take two, so aligned columns stay aligned in Chinese.</summary>
public static class TextWidth
{
    public static int Of(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += IsWide(rune.Value) ? 2 : 1;
        return width;
    }

    public static string PadRight(string text, int width) => text + new string(' ', Math.Max(0, width - Of(text)));
    public static string PadLeft(string text, int width) => new string(' ', Math.Max(0, width - Of(text))) + text;

    private static bool IsWide(int c) =>
        c is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6
            or >= 0x20000 and <= 0x3FFFD;
}
