using ContextWave.Core;

namespace ContextWave.Tests;

public class MarkdownDocumentTests
{
    /// <summary>One markdown parsed on its own: its sentences and the HTML that Append returned.</summary>
    private sealed record Parsed(IReadOnlyList<Sentence> Sentences, string Html);

    private static Parsed Parse(string markdown, bool announceCodeBlocks = true)
    {
        var added = new MarkdownDocument().Append(markdown, announceCodeBlocks);
        return new Parsed(added.Sentences, added.Html);
    }

    private static string[] Spoken(Parsed parsed) => parsed.Sentences.Select(s => s.SpokenText).ToArray();
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
    public void Mermaid_block_is_announced_as_a_diagram_and_keeps_its_language_class()
    {
        var doc = Parse("```Mermaid\nflowchart TD\n  A --> B\n```");
        Assert.Equal(new[] { SpeechRules.DiagramAnnouncement }, Spoken(doc));
        Assert.Contains("class=\"language-Mermaid\"", doc.Html);
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
    public void Relative_image_is_embedded_from_the_document_folder()
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "my images"));
            File.WriteAllBytes(Path.Combine(dir, "my images", "cat.png"), [1, 2, 3]);
            var doc = new MarkdownDocument(imageFolder: dir);
            var html = doc.Append("![A cat](my%20images/cat.png?v=1)").Html;

            Assert.Contains("src=\"data:image/png;base64,AQID\"", html);
            Assert.Contains("alt=\"A cat\"", html);
            Assert.Equal(new[] { "A cat" }, Spoken(doc));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Image_in_a_parent_folder_is_embedded()
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
            File.WriteAllText(Path.Combine(dir, "flow.svg"), "<svg/>");
            var doc = new MarkdownDocument(imageFolder: Path.Combine(dir, "docs"));
            var html = doc.Append("![Flow](../flow.svg)").Html;

            Assert.Contains("src=\"data:image/svg+xml;base64,PHN2Zy8+\"", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("![x](missing.png)", "missing.png")]
    [InlineData("![x](notes.txt)", "notes.txt")]
    [InlineData("![x](https://example.com/cat.png)", "https://example.com/cat.png")]
    public void Images_that_cannot_be_embedded_keep_their_url(string markdown, string url)
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not an image");
            var html = new MarkdownDocument(imageFolder: dir).Append(markdown).Html;

            Assert.Contains($"src=\"{url}\"", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"<\\host\share\a.png>")]
    [InlineData("<//host/share/a.png>")]
    [InlineData(@"<\\?\UNC\host\a.png>")]
    public void Images_on_a_network_or_device_path_are_dropped_from_the_page(string destination)
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            var html = new MarkdownDocument(imageFolder: dir).Append($"![x]({destination})").Html;

            // The page must never be told to fetch the host, and nothing may be embedded.
            Assert.Contains("<img", html);
            Assert.DoesNotContain("host", html);
            Assert.DoesNotContain("data:", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("%5C%5Chost%5Cshare%5Ca.png")]
    [InlineData("%2F%2Fhost%2Fshare%2Fa.png")]
    public void Escaped_network_paths_in_images_are_never_read(string destination)
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            var html = new MarkdownDocument(imageFolder: dir).Append($"![x]({destination})").Html;

            // Left as an inert relative reference (the page has no base URL), but never embedded.
            Assert.Contains($"src=\"{destination}\"", html);
            Assert.DoesNotContain("data:", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_image_on_another_drive_is_never_read()
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            var html = new MarkdownDocument(imageFolder: dir).Append("![x](C:/x.png)").Html;

            // A scheme-like drive prefix is stripped from the page, as any unsafe scheme is.
            Assert.Contains("<img", html);
            Assert.DoesNotContain("C:", html);
            Assert.DoesNotContain("data:", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("//host/share/x.md")]
    [InlineData(@"<\\host\share\x.md>")]
    public void Links_to_a_network_path_are_not_kept_relative(string destination)
    {
        var html = Parse($"[x]({destination})").Html;

        Assert.DoesNotContain("host", html);
    }

    [Fact]
    public void Images_are_not_embedded_without_a_document_folder() =>
        Assert.Contains("src=\"cat.png\"", Parse("![A cat](cat.png)").Html);

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

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](vbscript:msgbox(1))")]
    [InlineData("[click](data:text/html,hi)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("![x](javascript:alert(1))")]
    public void Unsafe_link_urls_never_reach_the_html(string markdown)
    {
        var html = Parse(markdown).Html;
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safe_link_urls_are_kept()
    {
        var html = Parse("[a](https://example.com/x) [b](mailto:me@example.com) [c](docs/page.md) [d](#top)").Html;
        Assert.Contains("href=\"https://example.com/x\"", html);
        Assert.Contains("href=\"mailto:me@example.com\"", html);
        Assert.Contains("href=\"docs/page.md\"", html);
        Assert.Contains("href=\"#top\"", html);
    }

    [Fact]
    public void Empty_table_cell_does_not_leave_a_dangling_comma() =>
        Assert.Equal(new[] { "A, B", "1" }, Spoken(Parse("| A | B |\n|---|---|\n| 1 |  |")));

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
    }

    [Fact]
    public void A_document_is_empty_until_something_is_appended()
    {
        var doc = new MarkdownDocument();
        Assert.True(doc.IsEmpty);

        doc.Append("One.");

        Assert.False(doc.IsEmpty);
    }

    [Fact]
    public void An_image_used_twice_is_read_once_and_counted_once()
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            var path = Path.Combine(dir, "big.png");
            File.WriteAllBytes(path, new byte[MarkdownDocument.MaxImageBytes]);
            var doc = new MarkdownDocument(imageFolder: dir);
            var images = string.Concat(Enumerable.Repeat("![x](big.png) ", 20));

            var html = doc.Append(images).Html;

            Assert.Equal(20, html.Split("src=\"data:image/png;base64,").Length - 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Images_past_the_document_limit_keep_their_url()
    {
        var dir = Directory.CreateTempSubdirectory("contextwave-images-").FullName;
        try
        {
            var count = (int)(MarkdownDocument.MaxEmbeddedBytes / MarkdownDocument.MaxImageBytes) + 2;
            var markdown = "";
            for (var i = 0; i < count; i++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{i}.png"), new byte[MarkdownDocument.MaxImageBytes]);
                markdown += $"![x]({i}.png) ";
            }
            File.WriteAllBytes(Path.Combine(dir, "small.png"), [1, 2, 3]);
            var doc = new MarkdownDocument(imageFolder: dir);

            var first = doc.Append(markdown).Html;
            var later = doc.Append("![x](0.png) ![y](small.png)").Html;

            Assert.Equal(count - 2, first.Split("src=\"data:").Length - 1);
            Assert.Contains($"src=\"{count - 1}.png\"", first);
            // An image already embedded still is; nothing new is once the limit is reached.
            Assert.Equal(1, later.Split("src=\"data:").Length - 1);
            Assert.Contains("src=\"small.png\"", later);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
