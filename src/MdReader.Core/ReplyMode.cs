namespace MdReader.Core;

/// <summary>Which chats' replies are read aloud by themselves (the "Auto Read" setting).</summary>
public static class ReplyMode
{
    /// <summary>Nothing is read by itself: replies are added to their chats' tabs, with a dot.</summary>
    public const string Off = "off";

    /// <summary>Only the chat in front is read; other chats' replies wait in their tabs, with a dot.</summary>
    public const string Tab = "tab";

    /// <summary>Every chat's replies are read; a chat whose reply is read comes to the front.</summary>
    public const string All = "all";

    /// <summary>The "Auto Read" menu, in order.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> Choices { get; } =
    [
        (Off, "Off"),
        (Tab, "This tab"),
        (All, "All chats"),
    ];

    /// <summary>The id when it is one of the choices, otherwise Off.</summary>
    public static string Normalize(string? id) => Choices.Any(c => c.Id == id) ? id! : Off;
}

/// <summary>
/// What happens when a reply, or a chat's request to read something, arrives while something is
/// being read (the "When something is playing" setting).
/// </summary>
public static class WhenPlaying
{
    /// <summary>Queued: after what is playing in its tab, or when its chat's turn comes.</summary>
    public const string Wait = "wait";

    /// <summary>It is read at once: what was being read stops where it is, and its chat comes forward.</summary>
    public const string Interrupt = "interrupt";

    /// <summary>The "When something is playing" menu, in order.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> Choices { get; } =
    [
        (Wait, "Wait your turn"),
        (Interrupt, "Interrupt"),
    ];

    /// <summary>The id when it is one of the choices, otherwise Wait.</summary>
    public static string Normalize(string? id) => Choices.Any(c => c.Id == id) ? id! : Wait;
}
