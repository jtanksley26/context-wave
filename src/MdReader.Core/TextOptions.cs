using System.Globalization;

namespace MdReader.Core;

/// <param name="Family">The font's name on the system.</param>
/// <param name="Generic">The CSS generic family to fall back to.</param>
public sealed record FontChoice(string Id, string DisplayName, string Family, string Generic);

/// <summary>The four text choices after unknown values have been replaced by defaults.</summary>
public sealed record ResolvedText(
    int Size, FontChoice Font, string WidthId, string WidthCss, string SpacingId, string SpacingCss)
{
    /// <summary>The page's CSS variables, without the leading "--".</summary>
    public IReadOnlyDictionary<string, string> PageVariables() => new Dictionary<string, string>
    {
        ["size"] = (TextOptions.BasePixels * Size / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "px",
        ["font"] = Font.Id == TextOptions.DefaultFontId
            ? $"\"{Font.Family}\", {Font.Generic}"
            : $"\"{Font.Family}\", \"Segoe UI\", {Font.Generic}",
        ["col"] = WidthCss,
        ["lh"] = SpacingCss,
    };
}

public static class TextOptions
{
    public const int DefaultSize = 100;

    /// <summary>The reading text's size in pixels at 100%.</summary>
    public const int BasePixels = 17;

    public const string DefaultFontId = "segoe";
    public const string DefaultWidthId = "medium";
    public const string DefaultSpacingId = "normal";

    /// <summary>The Text size menu, as percentages.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [80, 90, 100, 115, 130, 150, 175, 200];

    /// <summary>Segoe UI is always offered; the others only when installed.</summary>
    public static IReadOnlyList<FontChoice> Fonts { get; } =
    [
        new(DefaultFontId, "Segoe UI", "Segoe UI", "sans-serif"),
        new("verdana", "Verdana", "Verdana", "sans-serif"),
        new("georgia", "Georgia", "Georgia", "serif"),
        new("sitka", "Sitka Text", "Sitka Text", "serif"),
        new("atkinson", "Atkinson Hyperlegible", "Atkinson Hyperlegible", "sans-serif"),
        new("atkinson-next", "Atkinson Hyperlegible Next", "Atkinson Hyperlegible Next", "sans-serif"),
        new("opendyslexic", "OpenDyslexic", "OpenDyslexic", "sans-serif"),
        new("lexend", "Lexend", "Lexend", "sans-serif"),
    ];

    /// <summary>Widths are in em so the column grows with the text; 44.7em is 760px at 100%.</summary>
    public static IReadOnlyList<(string Id, string DisplayName, string Css)> Widths { get; } =
    [
        ("narrow", "Narrow", "34em"),
        (DefaultWidthId, "Medium", "44.7em"),
        ("wide", "Wide", "58em"),
        ("full", "Full width", "none"),
    ];

    public static IReadOnlyList<(string Id, string DisplayName, string Css)> Spacings { get; } =
    [
        ("compact", "Compact", "1.4"),
        (DefaultSpacingId, "Normal", "1.65"),
        ("relaxed", "Relaxed", "1.9"),
    ];

    /// <summary>The listed size nearest to <paramref name="percent"/>.</summary>
    public static int NormalizeSize(int percent) => Sizes.MinBy(size => Math.Abs((long)size - percent));

    /// <summary>The next (positive direction) or previous (negative) listed size, stopping at the ends.</summary>
    public static int StepSize(int percent, int direction)
    {
        var sizes = Sizes.ToList();
        var index = sizes.IndexOf(NormalizeSize(percent));
        return sizes[Math.Clamp(index + Math.Sign(direction), 0, sizes.Count - 1)];
    }

    /// <summary>The fonts to offer, in list order, given the families installed on the system.</summary>
    public static IReadOnlyList<FontChoice> AvailableFonts(IEnumerable<string> installedFamilies)
    {
        var installed = new HashSet<string>(installedFamilies, StringComparer.OrdinalIgnoreCase);
        return Fonts.Where(f => f.Id == DefaultFontId || installed.Contains(f.Family)).ToList();
    }

    /// <summary>Unknown ids, and a font that is not available, resolve to the defaults.</summary>
    public static ResolvedText Resolve(
        int sizePercent, string? fontId, string? widthId, string? spacingId, IEnumerable<string> installedFamilies)
    {
        var fonts = AvailableFonts(installedFamilies);
        var font = fonts.FirstOrDefault(f => f.Id == fontId) ?? fonts.First(f => f.Id == DefaultFontId);
        var width = Widths.Any(w => w.Id == widthId) ? Widths.First(w => w.Id == widthId) : Widths.First(w => w.Id == DefaultWidthId);
        var spacing = Spacings.Any(s => s.Id == spacingId)
            ? Spacings.First(s => s.Id == spacingId)
            : Spacings.First(s => s.Id == DefaultSpacingId);
        return new ResolvedText(NormalizeSize(sizePercent), font, width.Id, width.Css, spacing.Id, spacing.Css);
    }
}
