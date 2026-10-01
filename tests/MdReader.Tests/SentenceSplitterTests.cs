using MdReader.Core;

namespace MdReader.Tests;

public class SentenceSplitterTests
{
    private static string[] Texts(string s) =>
        SentenceSplitter.Split(s).Select(r => s.Substring(r.Start, r.Length)).ToArray();

    [Fact]
    public void Splits_on_terminator_followed_by_capital() =>
        Assert.Equal(new[] { "Hello world.", "This is it." }, Texts("Hello world. This is it."));

    [Fact]
    public void Does_not_split_after_abbreviations() =>
        Assert.Single(Texts("Use e.g. Apples and Dr. Smith vs. Bob."));

    [Fact]
    public void Does_not_split_inside_numbers() =>
        Assert.Equal(new[] { "Pi is 3.14 exactly.", "Yes." }, Texts("Pi is 3.14 exactly. Yes."));

    [Fact]
    public void Keeps_runs_of_terminators_together() =>
        Assert.Equal(new[] { "Really?!", "Yes." }, Texts("Really?! Yes."));

    [Fact]
    public void Keeps_closing_quote_with_its_sentence() =>
        Assert.Equal(new[] { "He said \"Stop.\"", "Then left." }, Texts("He said \"Stop.\" Then left."));

    [Fact]
    public void Does_not_split_before_lowercase() =>
        Assert.Single(Texts("It works. maybe"));

    [Fact]
    public void Text_without_terminator_is_one_sentence() =>
        Assert.Equal(new[] { "no terminator" }, Texts("  no terminator "));

    [Fact]
    public void Whitespace_only_yields_nothing() =>
        Assert.Empty(Texts("   "));
}
