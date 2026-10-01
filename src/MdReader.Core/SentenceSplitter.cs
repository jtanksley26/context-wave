namespace MdReader.Core;

public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

public static class SentenceSplitter
{
    private const string Terminators = ".!?";
    private const string Closers = "\"')]”’";
    private const string Openers = "\"'([“‘";

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "no", "fig", "inc", "ltd",
    };

    public static IReadOnlyList<TextRange> Split(string text)
    {
        var ranges = new List<TextRange>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (Terminators.IndexOf(text[i]) < 0)
            {
                i++;
                continue;
            }

            var terminator = i;
            var end = i + 1;
            while (end < text.Length && (Terminators.IndexOf(text[end]) >= 0 || Closers.IndexOf(text[end]) >= 0))
                end++;

            if (IsBoundary(text, terminator, end))
            {
                AddTrimmed(ranges, text, start, end);
                start = end;
            }
            i = end;
        }
        AddTrimmed(ranges, text, start, text.Length);
        return ranges;
    }

    private static bool IsBoundary(string text, int terminator, int end)
    {
        if (end == text.Length) return true;
        if (!char.IsWhiteSpace(text[end])) return false;

        var next = end;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        if (next == text.Length) return true;
        if (!char.IsUpper(text[next]) && Openers.IndexOf(text[next]) < 0) return false;

        var singleDot = text[terminator] == '.' && end == terminator + 1;
        return !(singleDot && Abbreviations.Contains(WordBefore(text, terminator)));
    }

    private static string WordBefore(string text, int index)
    {
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        return text.Substring(start, index - start).TrimStart(Openers.ToCharArray());
    }

    private static void AddTrimmed(List<TextRange> ranges, string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (end > start) ranges.Add(new TextRange(start, end - start));
    }
}
