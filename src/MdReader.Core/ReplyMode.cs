namespace MdReader.Core;

/// <summary>How the tab in front reads one of Claude's replies when it arrives.</summary>
public static class ReplyMode
{
    /// <summary>Replies are still added to their chats' tabs, but nothing is read aloud by itself.</summary>
    public const string Off = "off";

    /// <summary>A new reply interrupts and is read straight away.</summary>
    public const string Switch = "switch";

    /// <summary>A new reply is read once the one being read has finished.</summary>
    public const string Queue = "queue";

    /// <summary>A mode that was dropped; the nearest is Queue, since it never interrupted either.</summary>
    private const string Finish = "finish";

    /// <summary>The "Claude's replies" menu, in order.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> Choices { get; } =
    [
        (Off, "Off"),
        (Switch, "Read the newest right away"),
        (Queue, "Read after the current one"),
    ];

    /// <summary>The id when it is one of the modes, Queue for the old Finish, otherwise Off.</summary>
    public static string Normalize(string? id) =>
        id == Finish ? Queue : Choices.Any(c => c.Id == id) ? id! : Off;
}

/// <summary>Whether the app brings another chat's tab to the front when that chat replies.</summary>
public static class TabFocus
{
    /// <summary>Never: the reply is added to its tab, which is marked with a dot.</summary>
    public const string Stay = "stay";

    /// <summary>
    /// Once the tab in front has nothing left to read, the chats with unread replies come forward
    /// one after another, in the order their replies arrived, each read from its first unread reply.
    /// </summary>
    public const string Next = "next";

    /// <summary>The chat that replied comes forward at once and its new reply is read.</summary>
    public const string Newest = "newest";

    /// <summary>The "Tab focus" menu, in order.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> Choices { get; } =
    [
        (Stay, "Stay on this tab"),
        (Next, "Move to the next chat when done"),
        (Newest, "Jump to the newest"),
    ];

    /// <summary>The id when it is one of the choices, otherwise Stay.</summary>
    public static string Normalize(string? id) => Choices.Any(c => c.Id == id) ? id! : Stay;
}
