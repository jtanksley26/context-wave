using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace MdReader.Core;

/// <summary>A slice of one inline's text. SentenceId is -1 for text outside any sentence.</summary>
internal readonly record struct SpanPiece(int SentenceId, int Start, int Length);

internal static class SpanWriter
{
    public static void Write(
        HtmlRenderer renderer, Inline node, string text, Dictionary<Inline, List<SpanPiece>> pieces)
    {
        if (!renderer.EnableHtmlForInline || !pieces.TryGetValue(node, out var list))
        {
            renderer.WriteEscape(text);
            return;
        }

        foreach (var piece in list)
        {
            var part = text.Substring(piece.Start, piece.Length);
            if (piece.SentenceId < 0)
            {
                renderer.WriteEscape(part);
                continue;
            }
            renderer.Write("<span data-sid=\"").Write(piece.SentenceId.ToString()).Write("\">");
            renderer.WriteEscape(part);
            renderer.Write("</span>");
        }
    }
}

internal sealed class SpanLiteralRenderer(Dictionary<Inline, List<SpanPiece>> pieces)
    : HtmlObjectRenderer<LiteralInline>
{
    protected override void Write(HtmlRenderer renderer, LiteralInline obj) =>
        SpanWriter.Write(renderer, obj, obj.Content.ToString(), pieces);
}

internal sealed class SpanCodeInlineRenderer(Dictionary<Inline, List<SpanPiece>> pieces)
    : HtmlObjectRenderer<CodeInline>
{
    protected override void Write(HtmlRenderer renderer, CodeInline obj)
    {
        if (renderer.EnableHtmlForInline) renderer.Write("<code>");
        SpanWriter.Write(renderer, obj, obj.Content, pieces);
        if (renderer.EnableHtmlForInline) renderer.Write("</code>");
    }
}
