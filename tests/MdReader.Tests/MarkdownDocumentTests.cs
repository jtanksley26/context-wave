using MdReader.Core;

namespace MdReader.Tests;

public class MarkdownDocumentTests
{
    private static MarkdownDocument Parse(string markdown, bool announceCodeBlocks = true)
    {
        var doc = new MarkdownDocument(announceCodeBlocks);
        doc.Append(markdown);
        return doc;
    }

    private static string[] Spoken(MarkdownDocument doc) => doc.Sentences.Select(s => s.SpokenText).ToArray();

    [Fact]
    public void Paragraph_becomes_sentences_with_pause_on_the_last()
    {
        var doc = Parse("One. Two.");
        Assert.Equal(new[] { "One.", "Two." }, Spoken(doc));
        Assert.Equal(new[] { 0, 1 }, doc.Sentences.Select(s => s.Id));
        Assert.Equal(new[] { 0, SpeechRules.ParagraphPauseMs }, doc.Sentences.Select(s => s.PauseAfterMs));
        Assert.Contains("<span data-sid=\"0\">One.</span>", doc.Html);
        Assert.Contains("<span data-sid=\"1\">Two.</span>", doc.Html);
    }

    [Fact]
    public void Heading_gets_the_long_pause()
    {
        var doc = Parse("# Title\n\nBody.");
        Assert.Equal(new[] { "Title", "Body." }, Spoken(doc));
        Assert.Equal(SpeechRules.HeadingPauseMs, doc.Sentences[0].PauseAfterMs);
    }

    [Fact]
    public void Sentence_crossing_emphasis_is_split_into_spans_with_one_id()
    {
        var doc = Parse("A **bold. Next** end.");
        Assert.Equal(new[] { "A bold.", "Next end." }, Spoken(doc));
        Assert.Contains(
            "<strong><span data-sid=\"0\">bold.</span> <span data-sid=\"1\">Next</span></strong>",
            doc.Html);
    }

    [Fact]
    public void Link_is_read_as_its_text() =>
        Assert.Equal(new[] { "See the docs now." }, Spoken(Parse("See [the docs](https://x.com) now.")));

    [Fact]
    public void Bare_url_is_skipped() =>
        Assert.Equal(new[] { "Visit today." }, Spoken(Parse("Visit https://example.com today.")));

    [Fact]
    public void Inline_code_is_read() =>
        Assert.Equal(new[] { "Run dotnet build now." }, Spoken(Parse("Run `dotnet build` now.")));

    [Fact]
    public void Code_block_is_announced_and_tagged()
    {
        var doc = Parse("~~~\nx = 1\n~~~");
        Assert.Equal(new[] { SpeechRules.CodeBlockAnnouncement }, Spoken(doc));
        Assert.Contains("data-sid=\"0\"", doc.Html);
    }

    [Fact]
    public void Code_block_is_silent_when_announcements_are_off() =>
        Assert.Empty(Parse("~~~\nx = 1\n~~~", announceCodeBlocks: false).Sentences);

    [Fact]
    public void Table_is_read_row_by_row() =>
        Assert.Equal(
            new[] { "Name, Age", "Ann, 30" },
            Spoken(Parse("| Name | Age |\n|---|---|\n| Ann | 30 |")));

    [Fact]
    public void Image_is_read_as_alt_text() =>
        Assert.Equal(new[] { "A cat" }, Spoken(Parse("![A cat](cat.png)")));

    [Fact]
    public void List_items_get_the_list_pause()
    {
        var doc = Parse("- First item.\n- Second item.");
        Assert.Equal(new[] { "First item.", "Second item." }, Spoken(doc));
        Assert.All(doc.Sentences, s => Assert.Equal(SpeechRules.ListItemPauseMs, s.PauseAfterMs));
    }

    [Fact]
    public void Block_quote_is_read_normally() =>
        Assert.Equal(new[] { "Quoted text." }, Spoken(Parse("> Quoted text.")));

    [Fact]
    public void Strikethrough_markers_are_removed() =>
        Assert.Equal(new[] { "gone stays" }, Spoken(Parse("~~gone~~ stays")));

    [Fact]
    public void Rules_and_emoji_only_blocks_are_skipped() =>
        Assert.Empty(Parse("---\n\n\U0001F389").Sentences);

    [Fact]
    public void Raw_html_is_escaped() =>
        Assert.DoesNotContain("<script>", Parse("<script>alert(1)</script>\n\nHi.").Html);

    [Fact]
    public void Append_continues_ids_and_returns_only_the_new_part()
    {
        var doc = new MarkdownDocument();
        doc.Append("One.");
        var second = doc.Append("Two.");

        Assert.Equal(1, Assert.Single(second.Sentences).Id);
        Assert.Contains("Two.", second.Html);
        Assert.DoesNotContain("One.", second.Html);
        Assert.Equal(2, doc.Sentences.Count);
        Assert.Contains("One.", doc.Html);
        Assert.Contains("Two.", doc.Html);
    }
}
