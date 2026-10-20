using MdReader.Core;

namespace MdReader.Tests;

public class ReplyModeTests
{
    [Fact]
    public void Auto_Read_choices_are_in_menu_order_and_default_to_off()
    {
        Assert.Equal(new[] { "off", "tab", "all" }, ReplyMode.Choices.Select(c => c.Id));
        Assert.Equal(new[] { "Off", "This tab", "All chats" }, ReplyMode.Choices.Select(c => c.DisplayName));
        Assert.Equal("all", ReplyMode.Normalize("all"));
        Assert.Equal("off", ReplyMode.Normalize(null));
        Assert.Equal("off", ReplyMode.Normalize("queue"));
    }

    [Fact]
    public void When_playing_choices_are_in_menu_order_and_default_to_waiting()
    {
        Assert.Equal(new[] { "wait", "interrupt" }, WhenPlaying.Choices.Select(c => c.Id));
        Assert.Equal(new[] { "Wait your turn", "Interrupt" }, WhenPlaying.Choices.Select(c => c.DisplayName));
        Assert.Equal("interrupt", WhenPlaying.Normalize("interrupt"));
        Assert.Equal("wait", WhenPlaying.Normalize(null));
        Assert.Equal("wait", WhenPlaying.Normalize("sideways"));
    }
}
