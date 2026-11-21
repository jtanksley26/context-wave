using ContextWave.Core;

namespace ContextWave.Tests;

public class PageMessageTests
{
    [Fact]
    public void Diff_messages_with_an_empty_line_mean_the_file_header() =>
        Assert.Equal(new PageMessage.DiffClicked(2, null), PageMessage.Parse("diff:2:"));

    [Fact]
    public void Diff_messages_carry_the_file_and_line() =>
        Assert.Equal(new PageMessage.DiffClicked(2, 7), PageMessage.Parse("diff:2:7"));

    [Fact]
    public void A_number_is_a_sentence_click() =>
        Assert.Equal(new PageMessage.SentenceClicked(12), PageMessage.Parse("12"));

    [Theory]
    [InlineData("text:up", 1)]
    [InlineData("text:down", -1)]
    [InlineData("text:reset", 0)]
    public void Text_messages_are_size_steps(string message, int step) =>
        Assert.Equal(new PageMessage.TextSize(step), PageMessage.Parse(message));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("text:")]
    [InlineData("text:sideways")]
    [InlineData("diff")]
    [InlineData("diff:1")]
    [InlineData("diff:x:1")]
    [InlineData("diff:1:x")]
    [InlineData("diff:1:2:3")]
    [InlineData("Diff:1:2")]
    public void Malformed_page_messages_are_ignored(string message) =>
        Assert.Null(PageMessage.Parse(message));

    [Fact]
    public void A_missing_page_message_is_ignored() => Assert.Null(PageMessage.Parse(null));
}
