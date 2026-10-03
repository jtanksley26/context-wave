using System.Globalization;
using MdReader.Core;

namespace MdReader.Tests;

public class TextOptionsTests
{
    private static readonly string[] Standard = ["Segoe UI", "Verdana", "Georgia", "Sitka Text"];

    [Fact]
    public void Lists_and_defaults()
    {
        Assert.Equal(new[] { 80, 90, 100, 115, 130, 150, 175, 200 }, TextOptions.Sizes);
        Assert.Equal(
            new[] { "segoe", "verdana", "georgia", "sitka", "atkinson", "atkinson-next", "opendyslexic", "lexend" },
            TextOptions.Fonts.Select(f => f.Id));
        Assert.Equal(new[] { "narrow", "medium", "wide", "full" }, TextOptions.Widths.Select(w => w.Id));
        Assert.Equal(new[] { "Narrow", "Medium", "Wide", "Full width" }, TextOptions.Widths.Select(w => w.DisplayName));
        Assert.Equal(new[] { "compact", "normal", "relaxed" }, TextOptions.Spacings.Select(s => s.Id));
        Assert.Equal(
            (100, 17, "segoe", "medium", "normal"),
            (TextOptions.DefaultSize, TextOptions.BasePixels, TextOptions.DefaultFontId,
                TextOptions.DefaultWidthId, TextOptions.DefaultSpacingId));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(97, 100)]
    [InlineData(92, 90)]
    [InlineData(140, 130)]
    [InlineData(0, 80)]
    [InlineData(-5, 80)]
    [InlineData(5000, 200)]
    [InlineData(int.MinValue, 80)]
    [InlineData(int.MaxValue, 200)]
    public void NormalizeSize_picks_the_nearest_listed_size(int percent, int expected)
    {
        Assert.Equal(expected, TextOptions.NormalizeSize(percent));
    }

    [Theory]
    [InlineData(100, 1, 115)]
    [InlineData(100, -1, 90)]
    [InlineData(200, 1, 200)]
    [InlineData(80, -1, 80)]
    [InlineData(97, 1, 115)]
    [InlineData(100, 5, 115)]
    [InlineData(100, -9, 90)]
    [InlineData(100, 0, 100)]
    public void StepSize_moves_one_step_and_stops_at_the_ends(int percent, int direction, int expected)
    {
        Assert.Equal(expected, TextOptions.StepSize(percent, direction));
    }

    [Fact]
    public void AvailableFonts_lists_the_default_and_whatever_is_installed()
    {
        Assert.Equal(new[] { "segoe" }, TextOptions.AvailableFonts([]).Select(f => f.Id));
        Assert.Equal(
            new[] { "segoe", "georgia", "lexend" },
            TextOptions.AvailableFonts(["LEXEND", "georgia", "Wingdings"]).Select(f => f.Id));
        Assert.Equal(
            TextOptions.Fonts.Select(f => f.Id),
            TextOptions.AvailableFonts(TextOptions.Fonts.Select(f => f.Family)).Select(f => f.Id));
    }

    [Fact]
    public void Defaults_resolve_to_todays_page()
    {
        var text = TextOptions.Resolve(100, "segoe", "medium", "normal", Standard);
        var vars = text.PageVariables();

        Assert.Equal(new[] { "col", "font", "lh", "size" }, vars.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("17px", vars["size"]);
        Assert.Equal("\"Segoe UI\", sans-serif", vars["font"]);
        Assert.Equal("44.7em", vars["col"]);
        Assert.Equal("1.65", vars["lh"]);
        Assert.Equal((100, "segoe", "medium", "normal"), (text.Size, text.Font.Id, text.WidthId, text.SpacingId));
    }

    [Theory]
    [InlineData(80, "13.6px")]
    [InlineData(115, "19.55px")]
    [InlineData(175, "29.75px")]
    [InlineData(200, "34px")]
    public void Size_becomes_pixels(int percent, string expected)
    {
        Assert.Equal(expected, TextOptions.Resolve(percent, null, null, null, Standard).PageVariables()["size"]);
    }

    [Fact]
    public void Each_choice_maps_to_its_css()
    {
        var text = TextOptions.Resolve(130, "georgia", "full", "relaxed", Standard);
        var vars = text.PageVariables();

        Assert.Equal("\"Georgia\", \"Segoe UI\", serif", vars["font"]);
        Assert.Equal("none", vars["col"]);
        Assert.Equal("1.9", vars["lh"]);
        Assert.Equal("34em", TextOptions.Resolve(100, null, "narrow", null, Standard).PageVariables()["col"]);
        Assert.Equal("58em", TextOptions.Resolve(100, null, "wide", null, Standard).PageVariables()["col"]);
        Assert.Equal("1.4", TextOptions.Resolve(100, null, null, "compact", Standard).PageVariables()["lh"]);
    }

    [Fact]
    public void Unknown_choices_and_a_font_that_is_not_installed_fall_back()
    {
        var text = TextOptions.Resolve(120, "comic", "huge", "tight", Standard);
        Assert.Equal((115, "segoe", "medium", "normal"), (text.Size, text.Font.Id, text.WidthId, text.SpacingId));

        Assert.Equal("segoe", TextOptions.Resolve(100, "lexend", null, null, Standard).Font.Id);
        Assert.Equal("lexend", TextOptions.Resolve(100, "lexend", null, null, ["Lexend"]).Font.Id);
        Assert.Equal("segoe", TextOptions.Resolve(100, "", "", "", []).Font.Id);
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("19.55px", TextOptions.Resolve(115, null, null, null, Standard).PageVariables()["size"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
