using System.Text.Json;

namespace MdReader.Core;

/// <summary>
/// What Context Wave is doing right now, written to <see cref="AppPaths.LiveFile"/> for other
/// programs to show (the Claude desktop mod draws its orb from it).
/// </summary>
/// <param name="State">idle, playing or paused.</param>
/// <param name="Tab">The title of the tab in front.</param>
/// <param name="Source">A file path, "stream", "reply" or "" (see <see cref="ReaderSession.Source"/>).</param>
/// <param name="Sentence">The text of the sentence being read, or where reading would resume.</param>
/// <param name="Current">1-based position of that sentence; 0 when there is none.</param>
/// <param name="Total">Sentences in the document.</param>
/// <param name="Level">Loudness, 0 to 1.</param>
/// <param name="Bands">Pitch bands from low to high, each 0 to 1.</param>
/// <param name="Nearby">The sentences around <paramref name="Sentence"/>, for a reader to show in context.</param>
/// <param name="At">Where <paramref name="Sentence"/> is in <paramref name="Nearby"/>.</param>
public sealed record LiveSnapshot(
    string State, string Tab, string Source, string Sentence, int Current, int Total, float Level, float[] Bands,
    IReadOnlyList<string> Nearby, int At)
{
    public const int Before = 3;
    public const int After = 5;

    public static LiveSnapshot Idle { get; } =
        new("idle", "", "", "", 0, 0, 0f, new float[AudioAnalyzer.BandCount], [], 0);

    /// <summary>The snapshot of a tab, with the sound of the frame being played.</summary>
    public static LiveSnapshot Of(ChatTab? tab, AudioFrame frame)
    {
        if (tab is null) return Idle with { Level = frame.Level, Bands = frame.Bands };
        var queue = tab.Queue;
        var total = queue.Count;
        var (nearby, at) = queue.TextsAround(Before, After);
        return new LiveSnapshot(
            queue.State.ToString().ToLowerInvariant(),
            tab.Title,
            tab.Session.Source,
            queue.CurrentText ?? "",
            total == 0 ? 0 : Math.Min(queue.CurrentIndex + 1, total),
            total,
            frame.Level,
            frame.Bands,
            nearby,
            at);
    }

    /// <summary>Equal apart from the sound, which changes on every frame while playing.</summary>
    public bool SameReading(LiveSnapshot other) =>
        (State, Tab, Source, Sentence, Current, Total) ==
        (other.State, other.Tab, other.Source, other.Sentence, other.Current, other.Total);
}

/// <summary>
/// Keeps <see cref="AppPaths.LiveFile"/> up to date: at once when the reading changes, on every
/// call while there is sound, and otherwise once per <see cref="Heartbeat"/> so a reader can tell
/// a quiet window from a closed one by the file's age.
/// </summary>
public sealed class LiveStatusWriter(string path, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private LiveSnapshot? _last;
    private DateTimeOffset _lastWrite;

    /// <summary>True when the snapshot was written.</summary>
    public bool Publish(LiveSnapshot snapshot)
    {
        var now = _clock();
        var hasSound = snapshot.Level > 0 || _last is { Level: > 0 };
        if (_last is not null && _last.SameReading(snapshot) && !hasSound && now - _lastWrite < Heartbeat)
            return false;

        var text = JsonSerializer.Serialize(new
        {
            snapshot.State, snapshot.Tab, snapshot.Source, snapshot.Sentence, snapshot.Current,
            snapshot.Total, Level = Round(snapshot.Level), Bands = snapshot.Bands.Select(Round),
            snapshot.Nearby, snapshot.At,
            UpdatedAt = now.ToUnixTimeMilliseconds(),
        }, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written beside and moved over, so a reader never sees half a file.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text, PipeProtocol.Utf8);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A reader holding the file open is no reason to stop; the next call tries again.
            return false;
        }
        _last = snapshot;
        _lastWrite = now;
        return true;
    }

    /// <summary>Removes the file, so readers see Context Wave as closed.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The heartbeat stops, which readers take as closed too.
        }
    }

    // "NaN" is not valid JSON, and three places are plenty for a drawing.
    private static double Round(float value) => float.IsFinite(value) ? Math.Round(value, 3) : 0;
}
