using MdReader.Core;

namespace MdReader.Tests;

public class ReplyModeTests
{
    [Fact]
    public void Choices_are_the_four_modes_in_menu_order()
    {
        Assert.Equal(new[] { "off", "switch", "queue", "finish" }, ReplyMode.Choices.Select(c => c.Id));
        Assert.Equal(
            new[] { "Off", "Switch to the newest", "Queue", "Finish the current one" },
            ReplyMode.Choices.Select(c => c.DisplayName));
        Assert.Equal(
            ("off", "switch", "queue", "finish"),
            (ReplyMode.Off, ReplyMode.Switch, ReplyMode.Queue, ReplyMode.Finish));
    }

    [Theory]
    [InlineData("queue", "queue")]
    [InlineData("off", "off")]
    [InlineData(null, "off")]
    [InlineData("", "off")]
    [InlineData("loud", "off")]
    public void Normalize_keeps_known_modes_and_turns_the_rest_off(string? id, string expected)
    {
        Assert.Equal(expected, ReplyMode.Normalize(id));
    }
}
