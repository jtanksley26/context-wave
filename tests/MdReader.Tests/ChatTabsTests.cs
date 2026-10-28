using System.Collections.Concurrent;
using MdReader.Core;

namespace MdReader.Tests;

/// <summary>Routing, tabs and turn order. Every settings combination is in <see cref="ChatTabsSettingsMatrixTests"/>.</summary>
public sealed class ChatTabsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdreader-tabs-").FullName;
    private readonly FakeOutput _output = new() { Manual = true };
    private readonly FakeTts _tts = new();
    private readonly ChatTabs _tabs;
    private readonly List<ChatTab?> _activated = [];
    private readonly ConcurrentQueue<Action> _dispatched = new();
    private string _autoRead = "tab";
    private string _whenPlaying = "interrupt";
    private bool _announce = true;

    public ChatTabsTests()
    {
        _tabs = new ChatTabs(
            () => new ReadingQueue(_tts, _output, () => new VoiceSettings("m", 0, 1f), (_, _) => Task.CompletedTask),
            () => _announce, () => true, () => _autoRead, () => _whenPlaying, _dispatched.Enqueue);
        _tabs.ActiveChanged += _activated.Add;
    }

    public void Dispose()
    {
        foreach (var tab in _tabs.Tabs.ToList()) tab.Queue.Stop();
        Directory.Delete(_dir, recursive: true);
    }

    private PipeResponse Reply(string session, string text, string? folder = null, string? process = null, string? name = null) =>
        _tabs.Handle(new PipeRequest
        {
            Op = "speak_reply", Text = text, Session = session, Folder = folder, Process = process, Name = name,
        });

    private PipeResponse Speak(string text, string? process, string? folder = null) =>
        _tabs.Handle(new PipeRequest { Op = "speak", Text = text, Mode = "append", Process = process, Folder = folder });

    private ChatTab Tab(string session) => _tabs.Tabs.Single(t => t.SessionId == session);

    /// <summary>Lets each sentence finish as it is played until the condition holds.</summary>
    private Task PlayUntil(Func<bool> condition) => TestUtil.WaitUntil(() =>
    {
        while (_dispatched.TryDequeue(out var action)) action();
        _output.Release();
        return condition();
    });

    /// <summary>How many sentences have played (FakeTts makes each clip as long as its text).</summary>
    private int PlayedCount => _output.Played.Count;

    private static bool IsDoneReading(ChatTab tab) =>
        tab.Queue.State == ReadingState.Idle && tab.Queue.CurrentIndex >= tab.Queue.Count;

    [Fact]
    public void A_tab_is_named_after_its_chat_and_follows_a_rename()
    {
        Reply("s1", "One.", @"D:\Projects\Git\sql", name: "POS-50 branch breakdown");
        Assert.Equal("POS-50 branch breakdown", Tab("s1").Title);

        _whenPlaying = "wait";
        Reply("s1", "Two.", @"D:\Projects\Git\sql", name: "POS-50 branch merge");
        Assert.Equal("POS-50 branch merge", Tab("s1").Title);
    }

    [Fact]
    public void A_tab_keeps_its_chat_name_when_a_later_reply_has_none()
    {
        Reply("s1", "One.", "sql", name: "Ticket triage");
        _whenPlaying = "wait";
        Reply("s1", "Two.", "sql");

        Assert.Equal("Ticket triage", Tab("s1").Title);
    }

    [Fact]
    public void A_reply_opens_a_tab_named_after_its_folder_and_reads_it()
    {
        var response = Reply("s1", "One. Two.", @"D:\Projects\Git\sql");

        Assert.StartsWith("Reading the reply", response.Result!.Message);
        var tab = Assert.Single(_tabs.Tabs);
        Assert.Equal("sql", tab.Title);
        Assert.Equal("session:s1", tab.Key);
        Assert.Same(tab, _tabs.Active);
        Assert.Equal(ReadingState.Playing, tab.Queue.State);
        Assert.Contains("data-sid", tab.Html);
    }

    [Fact]
    public void Selecting_a_tab_marks_it_read_and_stops_the_other_tab_where_it_was()
    {
        Reply("s1", "One. Two.", "a");
        Reply("s2", "Three.", "b");

        _tabs.Select(Tab("s2"));

        Assert.False(Tab("s2").Unread);
        Assert.Equal(ReadingState.Idle, Tab("s1").Queue.State);
        Assert.Equal(2, Tab("s1").Queue.Count);
        Assert.Equal(0, Tab("s1").Queue.CurrentSentenceId);
        Assert.Equal(0, Tab("s2").Queue.CurrentSentenceId);
    }

    [Fact]
    public void Jump_to_the_newest_reads_only_the_new_reply_not_older_unread_ones()
    {
        Reply("s1", "One.", "a");
        Reply("s2", "Old one. Old two.", "b");
        _autoRead = "all";
        Reply("s2", "Newest.", "b");

        Assert.Equal("s2", _tabs.Active!.SessionId);
        Assert.Equal(2, Tab("s2").Queue.CurrentSentenceId);
    }

    [Fact]
    public async Task Move_to_next_goes_through_the_waiting_chats_in_the_order_their_replies_arrived()
    {
        _autoRead = "all";
        _whenPlaying = "wait";
        var order = new List<string?>();
        _tabs.ActiveChanged += tab => order.Add(tab?.SessionId);
        Reply("s1", "One.", "a");
        Reply("s3", "Three.", "c");
        Reply("s2", "Two.", "b");
        Reply("s3", "Three again.", "c");

        await PlayUntil(() => order.Count >= 3 && IsDoneReading(Tab("s2")));
        Assert.Equal(new[] { "s1", "s3", "s2" }, order);
        Assert.Equal(2, Tab("s3").Queue.Count);
    }

    [Fact]
    public async Task Move_to_next_finishes_a_reply_added_to_the_tab_in_front_before_moving_on()
    {
        _autoRead = "all";
        _whenPlaying = "wait";
        Reply("s1", "One.", "a");
        Reply("s2", "Two.", "b");
        Reply("s1", "One more.", "a");

        await PlayUntil(() => _tabs.Active!.SessionId == "s2");
        Assert.True(IsDoneReading(Tab("s1")));
        Assert.Equal(2, Tab("s1").Queue.Count);
    }

    [Fact]
    public async Task Move_to_next_reads_a_chat_from_its_first_unread_reply_never_from_halfway()
    {
        Reply("s1", "One.", "a");
        Reply("s2", "Two a. Two b.", "b");
        _tabs.Select(Tab("s2"));
        Tab("s2").Queue.Play();
        await PlayUntil(() => Tab("s2").Queue.CurrentIndex >= 1);
        _tabs.Select(Tab("s1"));                      // s2 is left halfway through
        Reply("s2", "Three.", "b");                   // and gets a new reply in the background
        Tab("s1").Queue.Stop();

        _autoRead = "all";
        _whenPlaying = "wait";
        Reply("s9", "Nine.", "z");                    // anything arriving while s1 is done moves on

        Assert.Equal("s2", _tabs.Active!.SessionId);
        Assert.Equal(2, Tab("s2").Queue.CurrentSentenceId);
    }

    [Fact]
    public void Choosing_a_tab_takes_it_out_of_the_line()
    {
        _autoRead = "all";
        _whenPlaying = "wait";
        Reply("s1", "One.", "a");
        Reply("s2", "Two.", "b");
        _tabs.Select(Tab("s2"));
        _tabs.Select(Tab("s1"));
        Tab("s1").Queue.Next();

        Reply("s3", "Three.", "c");

        Assert.Equal("s3", _tabs.Active!.SessionId);
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("interrupt")]
    public void With_auto_read_off_replies_are_collected_with_dots_and_focus_never_moves(string whenPlaying)
    {
        _whenPlaying = whenPlaying;
        _autoRead = "off";
        Reply("s1", "One.", "a");
        Reply("s2", "Two.", "b");

        Assert.Equal(2, _tabs.Tabs.Count);
        Assert.Equal("s1", _tabs.Active!.SessionId);
        Assert.True(Tab("s2").Unread);
        Assert.All(_tabs.Tabs, tab => Assert.Equal(ReadingState.Idle, tab.Queue.State));
    }

    [Fact]
    public void A_tool_call_goes_to_the_tab_of_the_chat_its_process_belongs_to()
    {
        Reply("s1", "One.", "a", "100@1");
        Reply("s2", "Two.", "b", "200@1");

        var response = Speak("Read this.", "100@1");

        Assert.True(response.Ok, response.Error);
        Assert.Equal(2, _tabs.Tabs.Count);
        Assert.Equal("s1", _tabs.Active!.SessionId);
        Assert.Contains("Read this", Tab("s1").Html);
    }

    [Fact]
    public void A_tool_call_brings_its_chats_tab_to_the_front()
    {
        Reply("s1", "One.", "a", "100@1");
        Reply("s2", "Two.", "b", "200@1");
        _tabs.Select(Tab("s2"));

        Speak("Read this.", "100@1");

        Assert.Equal("s1", _tabs.Active!.SessionId);
        Assert.Equal(ReadingState.Idle, Tab("s2").Queue.State);
    }

    [Fact]
    public void A_tool_call_from_a_chat_without_a_tab_opens_one_and_its_first_reply_takes_it_over()
    {
        Speak("Hello.", "300@7", @"C:\work\api");
        var opened = Assert.Single(_tabs.Tabs);
        Assert.Equal("process:300@7", opened.Key);
        Assert.Equal("api", opened.Title);

        _whenPlaying = "wait";
        Reply("s3", "Done.", @"C:\work\api", "300@7", name: "API cleanup");

        var tab = Assert.Single(_tabs.Tabs);
        Assert.Same(opened, tab);
        Assert.Equal("session:s3", tab.Key);
        Assert.Equal("s3", tab.SessionId);
        Assert.Equal("API cleanup", tab.Title);
        Assert.Equal(2, tab.Queue.Count);
    }

    [Fact]
    public void After_a_clear_the_chats_tool_calls_follow_its_new_session()
    {
        Reply("old", "One.", "a", "100@1");
        Reply("new", "Two.", "a", "100@1");

        Speak("Hello.", "100@1");

        Assert.Equal(2, _tabs.Tabs.Count);
        Assert.Contains("Hello", Tab("new").Html);
        Assert.DoesNotContain("Hello", Tab("old").Html);
    }

    [Fact]
    public void A_tool_call_without_a_process_goes_to_the_tab_in_front()
    {
        Reply("s1", "One.", "a");
        Speak("More.", process: null);

        var tab = Assert.Single(_tabs.Tabs);
        Assert.Contains("More", tab.Html);
    }

    [Fact]
    public void A_reply_without_a_session_goes_to_the_Claude_tab()
    {
        _tabs.Handle(new PipeRequest { Op = "speak_reply", Text = "One." });

        var tab = Assert.Single(_tabs.Tabs);
        Assert.Equal(ChatTabs.ClaudeKey, tab.Key);
        Assert.Equal("Claude", tab.Title);
    }

    [Fact]
    public void Status_and_stop_from_a_chat_without_a_tab_do_not_open_one()
    {
        var status = _tabs.Handle(new PipeRequest { Op = "status", Process = "9@9" });
        var stop = _tabs.Handle(new PipeRequest { Op = "stop", Process = "9@9" });

        Assert.True(status.Ok);
        Assert.Equal("idle", status.Result!.State);
        Assert.True(stop.Ok);
        Assert.Empty(_tabs.Tabs);
    }

    [Fact]
    public void Stop_from_a_chat_without_a_tab_leaves_the_tab_in_front_alone()
    {
        Reply("s1", "One. Two.", "a", "100@1");

        var stop = _tabs.Handle(new PipeRequest { Op = "stop", Process = "9@9" });

        Assert.True(stop.Ok);
        Assert.Equal(ReadingState.Playing, Tab("s1").Queue.State);
        Assert.Equal(2, Tab("s1").Queue.Count);
        Assert.Contains("data-sid", Tab("s1").Html);
    }

    [Fact]
    public void Stop_without_a_process_from_an_older_bridge_stops_the_tab_in_front()
    {
        Reply("s1", "One. Two.", "a", "100@1");

        _tabs.Handle(new PipeRequest { Op = "stop" });

        Assert.Equal(0, Tab("s1").Queue.Count);
    }

    [Fact]
    public void A_failed_tool_call_from_a_new_chat_leaves_no_tab()
    {
        Reply("s1", "One.", "a");
        var response = _tabs.Handle(new PipeRequest
        {
            Op = "read_file", Path = Path.Combine(_dir, "missing.md"), Process = "9@9",
        });

        Assert.False(response.Ok);
        Assert.Single(_tabs.Tabs);
        Assert.Equal("s1", _tabs.Active!.SessionId);
    }

    [Fact]
    public void A_failed_tool_call_from_a_new_chat_does_not_stop_the_chat_being_read()
    {
        Reply("s1", "One. Two.", "a");
        _tabs.Handle(new PipeRequest { Op = "read_file", Path = Path.Combine(_dir, "missing.md"), Process = "9@9" });

        Assert.Equal(ReadingState.Playing, Tab("s1").Queue.State);
    }

    [Fact]
    public void An_empty_reply_opens_no_tab()
    {
        var response = Reply("s1", "   ", "a");

        Assert.Contains("empty", response.Result!.Message);
        Assert.Empty(_tabs.Tabs);
    }

    [Fact]
    public void A_reply_with_nothing_to_read_opens_no_tab_and_does_not_switch()
    {
        _autoRead = "all";
        Reply("s1", "One.", "a");
        _announce = false;
        var response = Reply("s2", "~~~\ncode\n~~~", "b");

        Assert.Contains("nothing to read", response.Result!.Message);
        Assert.Single(_tabs.Tabs);
        Assert.Equal(ReadingState.Playing, Tab("s1").Queue.State);
    }

    [Fact]
    public void Closing_the_tab_in_front_brings_its_neighbour_forward()
    {
        Reply("s1", "One.", "a");
        Reply("s2", "Two.", "b");

        _tabs.Close(Tab("s1"));

        var left = Assert.Single(_tabs.Tabs);
        Assert.Same(left, _tabs.Active);
        Assert.False(left.Unread);
        Assert.Same(left, _activated.Last());
    }

    [Fact]
    public void A_closed_chat_gets_a_new_tab_with_its_next_reply()
    {
        Reply("s1", "One.", "a");
        _tabs.Close(Tab("s1"));
        Reply("s1", "Two.", "a");

        var tab = Assert.Single(_tabs.Tabs);
        Assert.Equal(1, tab.Queue.Count);
    }

    [Fact]
    public void A_file_opens_in_its_own_tab_and_opening_it_again_reuses_the_tab()
    {
        var path = Path.Combine(_dir, "notes.md");
        File.WriteAllText(path, "One. Two.");
        Reply("s1", "Reply.", "a");

        var tab = _tabs.OpenFile(path);
        _tabs.OpenFile(path);

        Assert.Equal(2, _tabs.Tabs.Count);
        Assert.Equal("notes.md", tab.Title);
        Assert.Same(tab, _tabs.Active);
        Assert.Equal(ReadingState.Idle, Tab("s1").Queue.State);
    }

    [Fact]
    public async Task With_wait_another_chats_tool_call_waits_its_turn_then_is_read_from_the_request()
    {
        Reply("s1", "One.", "a", "100@1");
        Reply("s2", "Reply.", "b", "200@1");
        _whenPlaying = "wait";

        var response = Speak("Read this.", "200@1");

        // s1 keeps reading; s2's request waits in its tab, with a dot...
        Assert.True(response.Ok, response.Error);
        Assert.Contains("when this chat's turn comes", response.Result!.Message);
        Assert.Equal("s1", _tabs.Active!.SessionId);
        Assert.Equal(ReadingState.Playing, Tab("s1").Queue.State);
        Assert.True(Tab("s2").Unread);
        Assert.Equal(ReadingState.Idle, Tab("s2").Queue.State);
        // ...and is read from the request, not from the older reply This tab left unread...
        Assert.Equal(1, Tab("s2").FirstUnreadId);
        // ...once s1 has finished.
        await PlayUntil(() => _tabs.Active!.SessionId == "s2");
        await PlayUntil(() => IsDoneReading(Tab("s2")));
        Assert.Equal(2, PlayedCount); // "One." and "Read this.": the older reply was not read
    }

    [Fact]
    public void Activate_raises_the_event_and_unknown_ops_fail()
    {
        var activated = false;
        _tabs.ActivateRequested += () => activated = true;

        Assert.True(_tabs.Handle(new PipeRequest { Op = "activate" }).Ok);
        Assert.True(activated);
        Assert.Contains("Unknown", _tabs.Handle(new PipeRequest { Op = "dance" }).Error);
    }

    [Fact]
    public void A_file_that_cannot_be_read_leaves_no_tab()
    {
        Assert.Throws<ReaderException>(() => _tabs.OpenFile(Path.Combine(_dir, "missing.md")));
        Assert.Empty(_tabs.Tabs);
    }

    [Fact]
    public void A_file_that_cannot_be_read_does_not_stop_the_chat_being_read()
    {
        Reply("s1", "One. Two.", "a");
        Assert.Throws<ReaderException>(() => _tabs.OpenFile(Path.Combine(_dir, "missing.md")));

        Assert.Equal(ReadingState.Playing, Tab("s1").Queue.State);
    }

    [Fact]
    public async Task A_waiting_diff_with_nothing_to_read_does_not_hold_up_the_chats_behind_it()
    {
        Reply("s1", "One.", "a", "100@1");
        Reply("s2", "Reply.", "b", "200@1");
        Reply("s3", "Reply.", "c", "300@1");
        _whenPlaying = "wait";

        Assert.True(_tabs.Handle(new PipeRequest { Op = "show_diff", Diff = SampleDiff.Foo, Process = "200@1" }).Ok);
        Speak("Read this.", "300@1");

        await PlayUntil(() => _tabs.Active!.SessionId == "s3" && IsDoneReading(Tab("s3")));
        Assert.NotEmpty(Tab("s2").DiffHtml);
    }
}
