using MdReader.Core;

namespace MdReader.Tests;

public class SpeechRulesTests
{
    [Theory]
    [InlineData("Hello <b>there</b>.", "Hello there.")]
    [InlineData("Done \U0001F389 now ✅.", "Done now .")]
    [InlineData("Visit https://example.com/a?b=1 today.", "Visit today.")]
    [InlineData("  spaced \n\t out  ", "spaced out")]
    public void Clean_removes_unspeakable_content(string input, string expected) =>
        Assert.Equal(expected, SpeechRules.Clean(input));

    [Theory]
    [InlineData("Hello.", true)]
    [InlineData("42", true)]
    [InlineData("---", false)]
    [InlineData("", false)]
    public void IsSpeakable_requires_a_letter_or_digit(string input, bool expected) =>
        Assert.Equal(expected, SpeechRules.IsSpeakable(input));
}
