using System.Text.RegularExpressions;

namespace MdReader.Core;

public static partial class SpeechRules
{
    public const int HeadingPauseMs = 600;
    public const int ParagraphPauseMs = 250;
    public const int ListItemPauseMs = 200;
    public const string CodeBlockAnnouncement = "code block";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex BareUrl();

    // Surrogate pairs cover most emoji; the BMP ranges cover arrows, symbols and dingbats.
    [GeneratedRegex(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|[←-⇿⌀-➿⬀-⯿️‍]")]
    private static partial Regex Emoji();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Clean(string text)
    {
        text = HtmlTag().Replace(text, "");
        text = BareUrl().Replace(text, "");
        text = Emoji().Replace(text, "");
        return Whitespace().Replace(text, " ").Trim();
    }

    public static bool IsSpeakable(string cleaned) => cleaned.Any(char.IsLetterOrDigit);
}
