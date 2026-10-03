using System.Globalization;

namespace MdReader.Core;

/// <summary>Which of a highlight colour's shades a theme uses.</summary>
public enum HighlightShade { Light, Dark, Contrast }

/// <summary>Colours are "#rrggbb". Surface is code blocks, diff file headers and hunk rows.</summary>
public sealed record Theme(
    string Id,
    string DisplayName,
    bool IsDark,
    HighlightShade HighlightShade,
    string Background,
    string Text,
    string Line,
    string Surface,
    string Added,
    string Removed,
    string Chrome,
    string ChromeText,
    string Control,
    string ControlBorder,
    string ControlHover,
    string Banner,
    string BannerText,
    string ErrorText);

/// <summary>Fill is the spoken sentence's background; bar is the edge of focused diff rows.</summary>
public sealed record HighlightColour(
    string Id,
    string DisplayName,
    string LightFill,
    string LightBar,
    string DarkFill,
    string DarkBar,
    string Contrast);

/// <param name="HighlightTint">The bar colour as a CSS rgba() value, laid over focused diff rows.</param>
public sealed record ResolvedTheme(
    Theme Theme,
    HighlightColour Highlight,
    string HighlightFill,
    string HighlightBar,
    string HighlightTint,
    string HighlightText)
{
    public bool IsDark => Theme.IsDark;

    /// <summary>The page's CSS variables, without the leading "--".</summary>
    public IReadOnlyDictionary<string, string> PageVariables() => new Dictionary<string, string>
    {
        ["bg"] = Theme.Background,
        ["fg"] = Theme.Text,
        ["line"] = Theme.Line,
        ["code"] = Theme.Surface,
        ["add"] = Theme.Added,
        ["del"] = Theme.Removed,
        ["hl"] = HighlightFill,
        ["hlfg"] = HighlightText,
        ["focus"] = HighlightBar,
        ["focusbg"] = HighlightTint,
    };
}

public static class ThemeCatalog
{
    public const string SystemId = "system";
    public const string DefaultHighlightId = "yellow";

    private const double TintOpacity = 0.22;

    public static IReadOnlyList<Theme> Themes { get; } =
    [
        new("light", "Light", false, HighlightShade.Light,
            "#ffffff", "#1f2328", "#d0d7de", "#f6f8fa", "#e6ffec", "#ffebe9",
            "#f3f3f3", "#1f2328", "#ffffff", "#c4c9cf", "#e5e9ee", "#fff4ce", "#3b3a39", "#b00020"),
        new("dark", "Dark", true, HighlightShade.Dark,
            "#1e1e1e", "#e6e6e6", "#444444", "#2a2a2a", "#12361f", "#4a1d1d",
            "#2b2b2b", "#e6e6e6", "#3a3a3a", "#555555", "#4a4a4a", "#4d3f00", "#f3e9c0", "#ff8a80"),
        new("dim", "Dim", true, HighlightShade.Dark,
            "#22272e", "#c9d1d9", "#444c56", "#2d333b", "#1b3a2a", "#4b2325",
            "#2d333b", "#c9d1d9", "#373e47", "#545d68", "#444c56", "#4a4020", "#eadfb8", "#ff938a"),
        new("sepia", "Sepia", false, HighlightShade.Light,
            "#f4ecd8", "#433422", "#d8c9a8", "#eae0c8", "#dcebc8", "#f3d4c8",
            "#eae0c8", "#433422", "#f8f1e0", "#c9b88f", "#dfd2b2", "#f1dfa0", "#433422", "#9a1b1b"),
        new("contrast", "High contrast", true, HighlightShade.Contrast,
            "#000000", "#ffffff", "#ffffff", "#1a1a1a", "#003d14", "#5c0000",
            "#000000", "#ffffff", "#000000", "#ffffff", "#333333", "#ffff00", "#000000", "#ff6b6b"),
    ];

    /// <summary>The Theme menu, in order: System first, then each concrete theme.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> ThemeChoices { get; } =
        [(SystemId, "System"), .. Themes.Select(t => (t.Id, t.DisplayName))];

    public static IReadOnlyList<HighlightColour> Highlights { get; } =
    [
        new("yellow", "Yellow", "#fff3a3", "#9a6700", "#5c4b00", "#e3b341", "#ffff00"),
        new("green", "Green", "#c8f0c8", "#2e7d32", "#1f4d2b", "#56d364", "#00ff66"),
        new("blue", "Blue", "#cfe4ff", "#1f6feb", "#1b3f73", "#79b8ff", "#66ccff"),
        new("pink", "Pink", "#ffd6e7", "#c2185b", "#5c2340", "#f778ba", "#ff80c0"),
        new("orange", "Orange", "#ffddb0", "#c45500", "#5e3410", "#ffa657", "#ffa500"),
        new("purple", "Purple", "#e6d6ff", "#7b3fc4", "#43307a", "#c297ff", "#d0a0ff"),
    ];

    /// <summary>True when the id is not one of the concrete themes, so the theme follows Windows.</summary>
    public static bool IsSystem(string? themeId) => Themes.All(t => t.Id != themeId);

    /// <summary>An unknown theme id resolves as System; an unknown highlight id as Yellow.</summary>
    public static ResolvedTheme Resolve(string? themeId, string? highlightId, bool systemIsDark)
    {
        var theme = Themes.FirstOrDefault(t => t.Id == themeId)
                    ?? Themes.First(t => t.Id == (systemIsDark ? "dark" : "light"));
        var highlight = Highlights.FirstOrDefault(h => h.Id == highlightId)
                        ?? Highlights.First(h => h.Id == DefaultHighlightId);

        var (fill, bar) = theme.HighlightShade switch
        {
            HighlightShade.Light => (highlight.LightFill, highlight.LightBar),
            HighlightShade.Dark => (highlight.DarkFill, highlight.DarkBar),
            _ => (highlight.Contrast, highlight.Contrast),
        };
        // The contrast fills are vivid, so text on them is black whatever the theme's text colour.
        var text = theme.HighlightShade == HighlightShade.Contrast ? "#000000" : theme.Text;

        var (r, g, b) = Rgb(bar);
        var tint = string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{TintOpacity})");
        return new ResolvedTheme(theme, highlight, fill, bar, tint, text);
    }

    public static (byte R, byte G, byte B) Rgb(string hex) => (
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    /// <summary>The WCAG contrast ratio between two colours, from 1 (identical) to 21.</summary>
    public static double Contrast(string hexA, string hexB)
    {
        var a = Luminance(hexA);
        var b = Luminance(hexB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }
}
