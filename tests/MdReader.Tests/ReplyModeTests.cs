using MdReader.Core;

namespace MdReader.Tests;

public class ReplyModeTests
{
    [Fact]
    public void Choices_are_the_three_modes_in_menu_order()
    {
        Assert.Equal(new[] { "off", "switch", "queue" }, ReplyMode.Choices.Select(c => c.Id));
        Assert.Equal(
            new[] { "Off", "Read the newest right away", "Read after the current one" },
            ReplyMode.Choices.Select(c => c.DisplayName));
        Assert.Equal(("off", "switch", "queue"), (ReplyMode.Off, ReplyMode.Switch, ReplyMode.Queue));
    }

    [Theory]
    [InlineData("queue", "queue")]
    [InlineData("off", "off")]
    [InlineData("finish", "queue")]
    [InlineData(null, "off")]
    [InlineData("", "off")]
    [InlineData("loud", "off")]
    public void Normalize_keeps_known_modes_moves_finish_to_queue_and_turns_the_rest_off(string? id, string expected)
    {
        Assert.Equal(expected, ReplyMode.Normalize(id));
    }

    [Fact]
    public void Focus_choices_are_in_menu_order_and_default_to_staying()
    {
        Assert.Equal(new[] { "stay", "next", "newest" }, TabFocus.Choices.Select(c => c.Id));
        Assert.Equal(
            new[] { "Stay on this tab", "Move to the next chat when done", "Jump to the newest" },
            TabFocus.Choices.Select(c => c.DisplayName));
        Assert.Equal("next", TabFocus.Normalize("next"));
        Assert.Equal("stay", TabFocus.Normalize(null));
        Assert.Equal("stay", TabFocus.Normalize("sideways"));
    }

    [Fact]
    public void Focus_defaults_to_stay_and_round_trips()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("mdreader-focus-").FullName, "settings.json");
        Assert.Equal("stay", Settings.Load(file).Focus);
        new Settings { Focus = "next" }.Save(file);
        Assert.Equal("next", Settings.Load(file).Focus);
        File.WriteAllText(file, "{\"Focus\":null}");
        Assert.Equal("stay", Settings.Load(file).Focus);
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }
}
