using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdReader.Core;

public sealed record Sentence(int Id, string SpokenText, int PauseAfterMs);

public sealed record AppendResult(IReadOnlyList<Sentence> Sentences, string Html);

public sealed class MarkdownDocument(bool announceCodeBlocks = true)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    private readonly record struct Segment(Inline Node, int Start, int Length);

    private readonly List<Sentence> _sentences = [];
    private readonly StringBuilder _html = new();
    private int _nextId;

    public IReadOnlyList<Sentence> Sentences => _sentences;
    public string Html => _html.ToString();

    /// <summary>
    /// Parses <paramref name="markdown"/> and appends its sentences and HTML. Each call is parsed
    /// independently, so callers should pass whole blocks (complete paragraphs, lists, fenced code
    /// blocks), not fragments.
    /// </summary>
    public AppendResult Append(string markdown)
    {
        var ast = Markdown.Parse(markdown, Pipeline);
        SanitizeLinks(ast);
        var added = new List<Sentence>();
        var pieces = new Dictionary<Inline, List<SpanPiece>>();
        foreach (var block in ast) Visit(block, added, pieces, inListItem: false);

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        // Index 0 so these win over Markdig's default literal and code renderers.
        renderer.ObjectRenderers.Insert(0, new SpanLiteralRenderer(pieces));
        renderer.ObjectRenderers.Insert(0, new SpanCodeInlineRenderer(pieces));
        renderer.Render(ast);
        writer.Flush();

        var html = writer.ToString();
        _sentences.AddRange(added);
        _html.Append(html);
        return new AppendResult(added, html);
    }

    private static void SanitizeLinks(MarkdownObject root)
    {
        foreach (var node in root.Descendants())
        {
            switch (node)
            {
                case LinkInline link when !IsSafeUrl(link.Url):
                    link.Url = "";
                    break;
                case AutolinkInline auto when !IsSafeUrl(auto.Url):
                    auto.Url = "";
                    break;
            }
        }
    }

    private static bool IsSafeUrl(string? url)
    {
        var trimmed = (url ?? "").Trim();
        var end = trimmed.IndexOfAny(['/', '?', '#']);
        var colon = trimmed.IndexOf(':');
        if (colon < 0 || (end >= 0 && end < colon)) return true; // no scheme: relative path or fragment
        var scheme = trimmed[..colon].Trim();
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    private void Visit(Block block, List<Sentence> added, Dictionary<Inline, List<SpanPiece>> pieces, bool inListItem)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AddLeaf(heading.Inline, SpeechRules.HeadingPauseMs, added, pieces);
                break;
            case ParagraphBlock paragraph:
                var pause = inListItem ? SpeechRules.ListItemPauseMs : SpeechRules.ParagraphPauseMs;
                AddLeaf(paragraph.Inline, pause, added, pieces);
                break;
            case CodeBlock code:
                if (announceCodeBlocks)
                {
                    var isDiagram = code is FencedCodeBlock fenced
                        && SpeechRules.DiagramLanguage.Equals(fenced.Info, StringComparison.OrdinalIgnoreCase);
                    var announcement = isDiagram ? SpeechRules.DiagramAnnouncement : SpeechRules.CodeBlockAnnouncement;
                    var sentence = new Sentence(_nextId++, announcement, SpeechRules.ParagraphPauseMs);
                    code.GetAttributes().AddProperty("data-sid", sentence.Id.ToString());
                    added.Add(sentence);
                }
                break;
            case Table table:
                foreach (var row in table.OfType<TableRow>()) AddRow(row, added, pieces);
                break;
            case ListItemBlock item:
                foreach (var child in item) Visit(child, added, pieces, inListItem: true);
                break;
            case ContainerBlock container:
                foreach (var child in container) Visit(child, added, pieces, inListItem);
                break;
        }
    }

    private void AddLeaf(
        ContainerInline? inline, int pauseMs, List<Sentence> added, Dictionary<Inline, List<SpanPiece>> pieces)
    {
        var plain = new StringBuilder();
        var segments = new List<Segment>();
        Collect(inline, plain, segments);
        var text = plain.ToString();
        AddSentences(text, SentenceSplitter.Split(text), segments, pauseMs, added, pieces);
    }

    private void AddRow(TableRow row, List<Sentence> added, Dictionary<Inline, List<SpanPiece>> pieces)
    {
        var plain = new StringBuilder();
        var segments = new List<Segment>();
        foreach (var cell in row.OfType<TableCell>())
        {
            var cellText = new StringBuilder();
            var cellSegments = new List<Segment>();
            foreach (var paragraph in cell.Descendants<ParagraphBlock>())
                Collect(paragraph.Inline, cellText, cellSegments);
            if (cellText.ToString().Trim().Length == 0) continue;
            if (plain.Length > 0) plain.Append(", ");
            var offset = plain.Length;
            plain.Append(cellText);
            foreach (var s in cellSegments) segments.Add(s with { Start = s.Start + offset });
        }
        var text = plain.ToString();
        AddSentences(text, [new TextRange(0, text.Length)], segments, SpeechRules.ParagraphPauseMs, added, pieces);
    }

    private void AddSentences(
        string text,
        IReadOnlyList<TextRange> ranges,
        List<Segment> segments,
        int pauseMs,
        List<Sentence> added,
        Dictionary<Inline, List<SpanPiece>> pieces)
    {
        var firstNew = added.Count;
        var mapped = new List<(TextRange Range, int Id)>();
        foreach (var range in ranges)
        {
            var spoken = SpeechRules.Clean(text.Substring(range.Start, range.Length));
            if (!SpeechRules.IsSpeakable(spoken)) continue;
            var sentence = new Sentence(_nextId++, spoken, 0);
            added.Add(sentence);
            mapped.Add((range, sentence.Id));
        }
        if (added.Count > firstNew) added[^1] = added[^1] with { PauseAfterMs = pauseMs };

        foreach (var segment in segments) pieces[segment.Node] = Slice(segment, mapped);
    }

    private static List<SpanPiece> Slice(Segment segment, List<(TextRange Range, int Id)> mapped)
    {
        var result = new List<SpanPiece>();
        var position = segment.Start;
        var segmentEnd = segment.Start + segment.Length;
        foreach (var (range, id) in mapped)
        {
            var start = Math.Max(range.Start, position);
            var end = Math.Min(range.End, segmentEnd);
            if (end <= start) continue;
            if (start > position) result.Add(new SpanPiece(-1, position - segment.Start, start - position));
            result.Add(new SpanPiece(id, start - segment.Start, end - start));
            position = end;
        }
        if (position < segmentEnd) result.Add(new SpanPiece(-1, position - segment.Start, segmentEnd - position));
        return result;
    }

    private static void Collect(ContainerInline? container, StringBuilder plain, List<Segment> segments)
    {
        if (container is null) return;
        foreach (var node in container)
        {
            switch (node)
            {
                case LiteralInline literal:
                    AddSegment(literal, literal.Content.ToString(), plain, segments);
                    break;
                case CodeInline code:
                    AddSegment(code, code.Content, plain, segments);
                    break;
                case LineBreakInline:
                    plain.Append(' ');
                    break;
                case AutolinkInline:
                case LinkInline { IsAutoLink: true }:
                    break;
                case LinkInline { IsImage: true } image:
                    plain.Append(string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString())));
                    break;
                case HtmlEntityInline entity:
                    plain.Append(entity.Transcoded.ToString());
                    break;
                case ContainerInline child:
                    Collect(child, plain, segments);
                    break;
            }
        }
    }

    private static void AddSegment(Inline node, string text, StringBuilder plain, List<Segment> segments)
    {
        segments.Add(new Segment(node, plain.Length, text.Length));
        plain.Append(text);
    }
}
