namespace MdReader.Core;

/// <summary>What to do with one of Claude's replies when another reply is still being read.</summary>
public static class ReplyMode
{
    public const string Off = "off";
    public const string Switch = "switch";
    public const string Queue = "queue";
    public const string Finish = "finish";

    /// <summary>The "Claude's replies" menu, in order.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> Choices { get; } =
    [
        (Off, "Off"),
        (Switch, "Switch to the newest"),
        (Queue, "Queue"),
        (Finish, "Finish the current one"),
    ];

    /// <summary>The id when it is one of the modes, otherwise Off.</summary>
    public static string Normalize(string? id) => Choices.Any(c => c.Id == id) ? id! : Off;
}
