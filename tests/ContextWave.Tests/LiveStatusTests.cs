using System.Text.Json;
using ContextWave.Core;

namespace ContextWave.Tests;

public sealed class LiveStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"live-{Guid.NewGuid():N}");
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);

    private string File => Path.Combine(_dir, "live.json");

    private LiveStatusWriter Writer() => new(File, () => _now);

    private static LiveSnapshot Reading(string sentence = "Hello there.", float level = 0f) =>
        new("playing", "Chat", @"C:\notes\a.md", sentence, 2, 5, level, new float[AudioAnalyzer.BandCount],
            ["First.", sentence, "Third."], 1);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_file_holds_what_is_being_read()
    {
        var bands = new float[AudioAnalyzer.BandCount];
        bands[3] = 0.25f;
        Assert.True(Writer().Publish(Reading(level: 0.5f) with { Bands = bands }));

        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(File));
        var root = json.RootElement;
        Assert.Equal("playing", root.GetProperty("state").GetString());
        Assert.Equal("Chat", root.GetProperty("tab").GetString());
        Assert.Equal(@"C:\notes\a.md", root.GetProperty("source").GetString());
        Assert.Equal("Hello there.", root.GetProperty("sentence").GetString());
        Assert.Equal((2, 5), (root.GetProperty("current").GetInt32(), root.GetProperty("total").GetInt32()));
        Assert.Equal(0.5, root.GetProperty("level").GetDouble());
        Assert.Equal(0.25, root.GetProperty("bands")[3].GetDouble());
        Assert.Equal(1_000_000, root.GetProperty("updatedAt").GetInt64());
        Assert.Equal(["First.", "Hello there.", "Third."],
            root.GetProperty("nearby").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(1, root.GetProperty("at").GetInt32());
        Assert.False(System.IO.File.Exists(File + ".tmp"));
    }

    [Fact]
    public void A_quiet_unchanged_reading_is_written_only_on_the_heartbeat()
    {
        var writer = Writer();
        Assert.True(writer.Publish(Reading()));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.False(writer.Publish(Reading()));
        _now += LiveStatusWriter.Heartbeat;
        Assert.True(writer.Publish(Reading()));
    }

    [Fact]
    public void A_new_sentence_is_written_at_once()
    {
        var writer = Writer();
        writer.Publish(Reading());
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(writer.Publish(Reading("Next one.")));
    }

    [Fact]
    public void Sound_is_written_on_every_call_and_once_more_when_it_stops()
    {
        var writer = Writer();
        writer.Publish(Reading(level: 0.4f));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(writer.Publish(Reading(level: 0.3f)));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(writer.Publish(Reading(level: 0f)));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.False(writer.Publish(Reading(level: 0f)));
    }

    [Fact]
    public void Clear_removes_the_file()
    {
        var writer = Writer();
        writer.Publish(Reading());
        writer.Clear();
        Assert.False(System.IO.File.Exists(File));
        writer.Clear();
    }

    [Fact]
    public void No_tab_is_idle()
    {
        var snapshot = LiveSnapshot.Of(null, AudioFrame.Silent);
        Assert.Equal(("idle", 0, 0), (snapshot.State, snapshot.Current, snapshot.Total));
    }

    /// <summary>A front tab reading "One. Two. Three." that stays on its sentence until it is moved.</summary>
    private static ChatTab ReadingTab()
    {
        var tts = new FakeTts();
        var output = new FakeOutput { Manual = true };
        var tabs = new ChatTabs(
            () => new ReadingQueue(tts, output, () => new VoiceSettings("m", 0, 1f), (_, _) => Task.CompletedTask),
            () => true, () => true, () => "tab", () => "wait", action => action());
        tabs.Handle(new PipeRequest
        {
            Op = "speak_reply", Text = "One. Two. Three.", Session = "s1", Folder = @"C:\work\notes", Process = "1@1",
        });
        return tabs.Active!;
    }

    [Fact]
    public void The_snapshot_of_a_tab_gives_its_position_text_and_neighbours()
    {
        var tab = ReadingTab();
        tab.Queue.Next();
        tab.Queue.Pause();

        var snapshot = LiveSnapshot.Of(tab, AudioFrame.Silent with { Level = 0.5f });

        Assert.Equal(("paused", "notes", "reply", "Two."), (snapshot.State, snapshot.Tab, snapshot.Source, snapshot.Sentence));
        Assert.Equal((2, 3), (snapshot.Current, snapshot.Total));
        Assert.Equal(0.5f, snapshot.Level);
        Assert.Equal(["One.", "Two.", "Three."], snapshot.Nearby);
        Assert.Equal(1, snapshot.At);
        tab.Queue.Stop();
    }

    [Fact]
    public void A_finished_tab_reports_its_last_sentence_not_one_past_it()
    {
        var tab = ReadingTab();
        tab.Queue.Next();
        tab.Queue.Next();
        tab.Queue.Next();

        var snapshot = LiveSnapshot.Of(tab, AudioFrame.Silent);

        Assert.Equal(("idle", 3, 3), (snapshot.State, snapshot.Current, snapshot.Total));
        Assert.Equal("", snapshot.Sentence);
        Assert.Equal(snapshot.Nearby.Count, snapshot.At);
        tab.Queue.Stop();
    }

    [Fact]
    public void A_pause_or_a_tab_change_is_written_at_once()
    {
        var writer = Writer();
        writer.Publish(Reading());
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(writer.Publish(Reading() with { State = "paused" }));
        _now += TimeSpan.FromMilliseconds(100);
        Assert.True(writer.Publish(Reading() with { Tab = "Other chat" }));
    }
}
