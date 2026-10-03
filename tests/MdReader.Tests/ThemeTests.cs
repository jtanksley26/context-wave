using System.Text.RegularExpressions;
using MdReader.Core;

namespace MdReader.Tests;

public class ThemeTests
{
    private static readonly Regex Hex = new("^#[0-9a-f]{6}$");

    public static TheoryData<string, string> Combinations()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in ThemeCatalog.Themes)
            foreach (var highlight in ThemeCatalog.Highlights)
                data.Add(theme.Id, highlight.Id);
        return data;
    }

    private static string Blend(string top, string bottom, double alpha)
    {
        var (tr, tg, tb) = ThemeCatalog.Rgb(top);
        var (br, bg, bb) = ThemeCatalog.Rgb(bottom);
        static int Mix(byte t, byte b, double a) => (int)Math.Round(t * a + b * (1 - a));
        return $"#{Mix(tr, br, alpha):x2}{Mix(tg, bg, alpha):x2}{Mix(tb, bb, alpha):x2}";
    }

    [Fact]
    public void Catalog_has_the_five_themes_and_six_highlights()
    {
        Assert.Equal(new[] { "light", "dark", "dim", "sepia", "contrast" }, ThemeCatalog.Themes.Select(t => t.Id));
        Assert.Equal(
            new[] { "yellow", "green", "blue", "pink", "orange", "purple" },
            ThemeCatalog.Highlights.Select(h => h.Id));
        Assert.Equal(
            new[] { "system", "light", "dark", "dim", "sepia", "contrast" },
            ThemeCatalog.ThemeChoices.Select(c => c.Id));
        Assert.Equal("System", ThemeCatalog.ThemeChoices[0].DisplayName);
        Assert.Equal("High contrast", ThemeCatalog.ThemeChoices[5].DisplayName);
    }

    [Fact]
    public void Every_colour_is_a_lowercase_hex_value()
    {
        foreach (var t in ThemeCatalog.Themes)
            foreach (var colour in new[]
                     {
                         t.Background, t.Text, t.Line, t.Surface, t.Added, t.Removed, t.Chrome, t.ChromeText,
                         t.Control, t.ControlBorder, t.ControlHover, t.Banner, t.BannerText, t.ErrorText,
                     })
                Assert.Matches(Hex, colour);

        foreach (var h in ThemeCatalog.Highlights)
            foreach (var colour in new[] { h.LightFill, h.LightBar, h.DarkFill, h.DarkBar, h.Contrast })
                Assert.Matches(Hex, colour);
    }

    [Fact]
    public void Dark_themes_are_marked_dark()
    {
        Assert.Equal(
            new[] { false, true, true, false, true },
            ThemeCatalog.Themes.Select(t => t.IsDark));
    }

    [Theory]
    [InlineData(false, "light")]
    [InlineData(true, "dark")]
    public void System_follows_windows(bool systemIsDark, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.Resolve("system", "yellow", systemIsDark).Theme.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("neon")]
    public void Unknown_theme_resolves_as_system(string? id)
    {
        Assert.Equal("dark", ThemeCatalog.Resolve(id, "yellow", systemIsDark: true).Theme.Id);
        Assert.Equal("light", ThemeCatalog.Resolve(id, "yellow", systemIsDark: false).Theme.Id);
        Assert.True(ThemeCatalog.IsSystem(id));
    }

    [Fact]
    public void A_chosen_theme_does_not_follow_windows()
    {
        Assert.Equal("sepia", ThemeCatalog.Resolve("sepia", "yellow", systemIsDark: true).Theme.Id);
        Assert.False(ThemeCatalog.IsSystem("sepia"));
        Assert.True(ThemeCatalog.IsSystem("system"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("teal")]
    public void Unknown_highlight_resolves_as_yellow(string? id)
    {
        Assert.Equal("yellow", ThemeCatalog.Resolve("light", id, false).Highlight.Id);
    }

    [Fact]
    public void Each_theme_uses_its_highlight_shade()
    {
        var blue = ThemeCatalog.Highlights.Single(h => h.Id == "blue");

        var light = ThemeCatalog.Resolve("light", "blue", false);
        Assert.Equal((blue.LightFill, blue.LightBar, light.Theme.Text),
            (light.HighlightFill, light.HighlightBar, light.HighlightText));

        var sepia = ThemeCatalog.Resolve("sepia", "blue", false);
        Assert.Equal((blue.LightFill, blue.LightBar), (sepia.HighlightFill, sepia.HighlightBar));

        var dim = ThemeCatalog.Resolve("dim", "blue", false);
        Assert.Equal((blue.DarkFill, blue.DarkBar, dim.Theme.Text),
            (dim.HighlightFill, dim.HighlightBar, dim.HighlightText));

        var contrast = ThemeCatalog.Resolve("contrast", "blue", false);
        Assert.Equal((blue.Contrast, blue.Contrast, "#000000"),
            (contrast.HighlightFill, contrast.HighlightBar, contrast.HighlightText));
    }

    [Fact]
    public void Tint_is_the_bar_colour_at_22_percent()
    {
        var resolved = ThemeCatalog.Resolve("light", "yellow", false);
        Assert.Equal("#9a6700", resolved.HighlightBar);
        Assert.Equal("rgba(154,103,0,0.22)", resolved.HighlightTint);
    }

    [Fact]
    public void Page_variables_cover_every_css_variable()
    {
        var resolved = ThemeCatalog.Resolve("dark", "green", false);
        var vars = resolved.PageVariables();

        Assert.Equal(
            new[] { "add", "bg", "code", "del", "fg", "focus", "focusbg", "hl", "hlfg", "line" },
            vars.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(resolved.Theme.Background, vars["bg"]);
        Assert.Equal(resolved.Theme.Text, vars["fg"]);
        Assert.Equal(resolved.Theme.Surface, vars["code"]);
        Assert.Equal(resolved.Theme.Removed, vars["del"]);
        Assert.Equal(resolved.HighlightFill, vars["hl"]);
        Assert.Equal(resolved.HighlightText, vars["hlfg"]);
        Assert.Equal(resolved.HighlightBar, vars["focus"]);
        Assert.Equal(resolved.HighlightTint, vars["focusbg"]);
    }

    [Theory]
    [InlineData("#000000", "#ffffff", 21.0)]
    [InlineData("#ffffff", "#000000", 21.0)]
    [InlineData("#777777", "#777777", 1.0)]
    [InlineData("#767676", "#ffffff", 4.54)]
    public void Contrast_matches_known_ratios(string a, string b, double expected)
    {
        Assert.Equal(expected, ThemeCatalog.Contrast(a, b), 2);
    }

    [Fact]
    public void Rgb_parses_a_hex_colour()
    {
        Assert.Equal(((byte)154, (byte)103, (byte)0), ThemeCatalog.Rgb("#9a6700"));
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_combination_is_readable(string themeId, string highlightId)
    {
        var r = ThemeCatalog.Resolve(themeId, highlightId, false);
        var t = r.Theme;

        var textPairs = new (string Name, string Text, string Background)[]
        {
            ("text on background", t.Text, t.Background),
            ("text on surface", t.Text, t.Surface),
            ("text on added", t.Text, t.Added),
            ("text on removed", t.Text, t.Removed),
            ("chrome text on chrome", t.ChromeText, t.Chrome),
            ("chrome text on control", t.ChromeText, t.Control),
            ("chrome text on hover", t.ChromeText, t.ControlHover),
            ("banner text on banner", t.BannerText, t.Banner),
            ("error text on chrome", t.ErrorText, t.Chrome),
            ("highlight text on fill", r.HighlightText, r.HighlightFill),
            ("text on focused row", t.Text, Blend(r.HighlightBar, t.Background, 0.22)),
            ("text on focused added row", t.Text, Blend(r.HighlightBar, t.Added, 0.22)),
            ("text on focused removed row", t.Text, Blend(r.HighlightBar, t.Removed, 0.22)),
        };
        foreach (var (name, text, background) in textPairs)
            Assert.True(
                ThemeCatalog.Contrast(text, background) >= 4.5,
                $"{themeId}/{highlightId}: {name} is {ThemeCatalog.Contrast(text, background):0.00}:1 ({text} on {background})");

        // The bar is a graphic, not text: 3:1 is the rule for those.
        Assert.True(
            ThemeCatalog.Contrast(r.HighlightBar, t.Background) >= 3.0,
            $"{themeId}/{highlightId}: bar is {ThemeCatalog.Contrast(r.HighlightBar, t.Background):0.00}:1");
    }
}
