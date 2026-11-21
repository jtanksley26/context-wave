namespace ContextWave.Core;

/// <summary>A message the page posts to the app.</summary>
public abstract record PageMessage
{
    public sealed record SentenceClicked(int Id) : PageMessage;

    /// <summary>A click in the diff: the file index, and the line index or null for the file header.</summary>
    public sealed record DiffClicked(int FileIndex, int? Line) : PageMessage;

    /// <summary>A size shortcut: +1 larger, -1 smaller, 0 back to the default.</summary>
    public sealed record TextSize(int Step) : PageMessage;

    /// <summary>A sentence id, "diff:{file}:{line}" (line empty for a header), or "text:up", "text:down", "text:reset".</summary>
    public static PageMessage? Parse(string? message)
    {
        if (message is null) return null;
        if (int.TryParse(message, out var id)) return new SentenceClicked(id);

        if (message.StartsWith("text:", StringComparison.Ordinal))
        {
            return message["text:".Length..] switch
            {
                "up" => new TextSize(1),
                "down" => new TextSize(-1),
                "reset" => new TextSize(0),
                _ => null,
            };
        }

        if (message.Split(':') is not ["diff", var file, var line] || !int.TryParse(file, out var fileIndex)) return null;
        if (line == "") return new DiffClicked(fileIndex, null);
        return int.TryParse(line, out var lineIndex) ? new DiffClicked(fileIndex, lineIndex) : null;
    }
}
