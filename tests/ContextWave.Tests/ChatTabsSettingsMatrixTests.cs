using ContextWave.Core;

namespace ContextWave.Tests;

/// <summary>
/// Every combination of the two reply settings (Auto Read and When something is playing) against every state the
/// tab in front can be in, with dummy chats. Besides each outcome, two things hold throughout:
/// only the tab in front ever reads, and no two sentences ever play at once.
/// </summary>
public sealed class ChatTabsSettingsMatrixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("contextwave-matrix-").FullName;
    private readonly OverlapOutput _output = new();
    private readonly ChatTabs _tabs;
    private string _autoRead = ReplyMode.Tab;
    private string _whenPlaying = WhenPlaying.Interrupt;

    // As in the app, the tabs are only touched from one thread: what a queue raises from its
    // playback thread waits here and is run by the test, like the window's dispatcher.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _dispatched = new();

    public ChatTabsSettingsMatrixTests()
    {
        _tabs = new ChatTabs(
            () => new ReadingQueue(new FakeTts(), _output, () => new VoiceSettings("m", 0, 1f), (_, _) => Task.CompletedTask),
            () => true, () => true, () => _autoRead, () => _whenPlaying, _dispatched.Enqueue);
    }

    private void RunDispatched()
    {
        while (_dispatched.TryDequeue(out var action)) action();
    }

    public void Dispose()
    {
        foreach (var tab in _tabs.Tabs.ToList()) tab.Queue.Stop();
        Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<string, string> AllSettings()
    {
        var data = new TheoryData<string, string>();
        foreach (var (replies, _) in ReplyMode.Choices)
            foreach (var (whenPlaying, _) in WhenPlaying.Choices)
                data.Add(replies, whenPlaying);
        return data;
    }

    /// <summary>What the tab in front is doing when a reply arrives.</summary>
    public enum Front { ReadingReply, PausedReply, DoneReply, ReadingFile, ReadingSpeak, ReadingWalkthrough, DoneWalkthrough }

    public static TheoryData<string, string, Front> AllSettingsAndFronts() => SettingsAnd(Enum.GetValues<Front>());

    /// <summary>A file tab is nobody's chat, so it cannot get a reply from the chat in front.</summary>
    public static TheoryData<string, string, Front> AllSettingsAndChatFronts() =>
        SettingsAnd(Enum.GetValues<Front>().Where(f => f != Front.ReadingFile));

    private static TheoryData<string, string, Front> SettingsAnd(IEnumerable<Front> fronts)
    {
        var data = new TheoryData<string, string, Front>();
        foreach (var front in fronts)
            foreach (var (replies, _) in ReplyMode.Choices)
                foreach (var (whenPlaying, _) in WhenPlaying.Choices)
                    data.Add(replies, whenPlaying, front);
        return data;
    }

    // ---- Dummy chats ----

    private const string Chat1 = "s1", Chat1Process = "100@1";
    private const string Chat2 = "s2", Chat2Process = "200@1";

    private PipeResponse Reply(string session, string process, string text) =>
        _tabs.Handle(new PipeRequest
        {
            Op = "speak_reply", Text = text, Session = session, Folder = $@"C:\work\{session}", Process = process,
        });

    private PipeResponse Tool(string op, string process, string? text = null, string? diff = null, string? focus = null) =>
        _tabs.Handle(new PipeRequest
        {
            Op = op, Text = text, Mode = "append", Diff = diff, Focus = focus, Process = process, Folder = @"C:\work\tool",
        });

    private ChatTab Tab(string session) => _tabs.Tabs.Single(t => t.SessionId == session);

    /// <summary>Puts chat 1 (or a file) in front in the given state, with neutral settings.</summary>
    private async Task<ChatTab> SetUpFront(Front front)
    {
        ChatTab tab;
        switch (front)
        {
            case Front.ReadingFile:
                var path = Path.Combine(_dir, "notes.md");
                File.WriteAllText(path, "File one. File two. File three.");
                return _tabs.OpenFile(path);
            case Front.ReadingSpeak:
                Assert.True(Tool("speak", Chat1Process, "Spoken one. Spoken two. Spoken three.").Ok);
                return _tabs.Active!;
            case Front.ReadingWalkthrough or Front.DoneWalkthrough:
                Assert.True(Tool("show_diff", Chat1Process, diff: SampleDiff.Foo).Ok);
                Assert.True(Tool("speak", Chat1Process, "About one. About two.", focus: "Foo.cs:2").Ok);
                tab = _tabs.Active!;
                if (front == Front.DoneWalkthrough) await PlayToSilence();
                return tab;
            default:
                Reply(Chat1, Chat1Process, "Reply one. Reply two. Reply three.");
                tab = Tab(Chat1);
                if (front == Front.PausedReply) tab.Queue.Pause();
                if (front == Front.DoneReply) await PlayToSilence();
                return tab;
        }
    }

    private static bool IsReading(Front front) =>
        front is Front.ReadingReply or Front.ReadingFile or Front.ReadingSpeak or Front.ReadingWalkthrough;

    /// <summary>Lets sentences finish one after another until the condition holds.</summary>
    private Task PlayUntil(Func<bool> condition) => TestUtil.WaitUntil(() =>
    {
        RunDispatched();
        _output.Release();
        AssertOneVoice();
        return condition();
    });

    /// <summary>
    /// Lets everything that will play finish, and returns once nothing has been reading for a few
    /// polls in a row: a queue goes idle a moment before it says it has finished, and what it
    /// does then (move to the next chat, read a held reply) must be given the chance to start.
    /// </summary>
    private Task PlayToSilence()
    {
        var quiet = 0;
        return PlayUntil(() =>
        {
            var silent = _dispatched.IsEmpty && !_output.Pending
                         && _tabs.Tabs.All(t => t.Queue.State != ReadingState.Playing);
            quiet = silent ? quiet + 1 : 0;
            return quiet >= 5;
        });
    }

    private void AssertOneVoice()
    {
        Assert.True(_output.MaxConcurrent <= 1, $"{_output.MaxConcurrent} sentences played at once.");
        foreach (var tab in _tabs.Tabs)
            if (tab != _tabs.Active) Assert.NotEqual(ReadingState.Playing, tab.Queue.State);
        if (_tabs.Active is { } active) Assert.False(active.Unread, "The tab in front is marked unread.");
    }

    // ---- Another chat replies ----

    [Theory]
    [MemberData(nameof(AllSettingsAndFronts))]
    public async Task Another_chats_reply(string autoRead, string whenPlaying, Front front)
    {
        var first = await SetUpFront(front);
        var stateBefore = first.Queue.State;
        _autoRead = autoRead;
        _whenPlaying = whenPlaying;

        var response = Reply(Chat2, Chat2Process, "Other one. Other two.");

        Assert.True(response.Ok);
        // Only All chats reads another chat; Interrupt brings it on at once, Wait once nothing is playing.
        var all = autoRead == ReplyMode.All;
        var interrupts = whenPlaying == WhenPlaying.Interrupt;
        var playing = front is not (Front.DoneReply or Front.DoneWalkthrough);
        var movesNow = all && (interrupts || !playing);

        var second = Tab(Chat2);
        if (movesNow)
        {
            Assert.Same(second, _tabs.Active);
            Assert.Equal(ReadingState.Playing, second.Queue.State);
            Assert.False(second.Unread);
            Assert.NotEqual(ReadingState.Playing, first.Queue.State);
        }
        else
        {
            Assert.Same(first, _tabs.Active);
            Assert.Equal(stateBefore, first.Queue.State);
            Assert.True(second.Unread);
            Assert.Equal(ReadingState.Idle, second.Queue.State);
        }
        AssertOneVoice();

        if (movesNow || front == Front.PausedReply) return;

        // Once the front tab has nothing left to read, All chats with Wait brings chat 2 on.
        var movesLater = all && !interrupts && IsReading(front);
        if (movesLater)
        {
            await PlayUntil(() => _tabs.Active == second && second.Queue.CurrentIndex >= 1);
            Assert.False(second.Unread);
        }
        else
        {
            await PlayToSilence();
            Assert.Same(first, _tabs.Active);
            Assert.True(second.Unread);
            Assert.Equal(0, second.Queue.CurrentIndex);
        }
        AssertOneVoice();
    }

    // ---- The chat in front replies ----

    [Theory]
    [MemberData(nameof(AllSettingsAndChatFronts))]
    public async Task The_same_chats_reply(string autoRead, string whenPlaying, Front front)
    {
        var tab = await SetUpFront(front);
        var stateBefore = tab.Queue.State;
        var countBefore = tab.Queue.Count;
        var placeBefore = tab.Queue.CurrentIndex;
        _autoRead = autoRead;
        _whenPlaying = whenPlaying;
        var off = autoRead == ReplyMode.Off;
        var interrupts = !off && whenPlaying == WhenPlaying.Interrupt;

        var message = Reply(Chat1, Chat1Process, "Same one. Same two.").Result!.Message!;

        // The chat in front: This tab and All chats read it alike, and no tab is added or moved to.
        Assert.Same(tab, _tabs.Active);
        Assert.Single(_tabs.Tabs);
        Assert.False(tab.Unread);

        switch (front)
        {
            case Front.ReadingWalkthrough:
                Assert.Equal("Skipped: a walkthrough is showing.", message);
                Assert.Equal(countBefore, tab.Queue.Count);
                Assert.Equal(stateBefore, tab.Queue.State);
                Assert.NotEqual("", tab.DiffHtml);
                break;
            case Front.DoneWalkthrough:
                Assert.Equal("", tab.DiffHtml);
                Assert.DoesNotContain("About one", tab.Html);
                Assert.Equal(2, tab.Queue.Count);
                Assert.Equal(off ? ReadingState.Idle : ReadingState.Playing, tab.Queue.State);
                break;
            case Front.ReadingSpeak or Front.ReadingReply or Front.PausedReply:
                // Spoken text and replies alike: Interrupt jumps to it, Wait queues it, Off holds it.
                Assert.Equal(countBefore + 2, tab.Queue.Count);
                if (interrupts)
                {
                    Assert.Equal(countBefore, tab.Queue.CurrentIndex);
                    Assert.Equal(ReadingState.Playing, tab.Queue.State);
                }
                else
                {
                    Assert.Contains(off ? "without reading" : "Queued", message);
                    Assert.Equal(placeBefore, tab.Queue.CurrentIndex);
                    Assert.Equal(stateBefore, tab.Queue.State);
                }
                break;
            case Front.DoneReply:
                Assert.Equal(countBefore + 2, tab.Queue.Count);
                Assert.Equal(off ? ReadingState.Idle : ReadingState.Playing, tab.Queue.State);
                break;
        }
        AssertOneVoice();

        if (front == Front.PausedReply && !interrupts) return;

        // Read to the end: is the new reply read, or left waiting? Read unless Auto Read is off.
        await PlayToSilence();
        var readNewReply = front != Front.ReadingWalkthrough && !off;
        if (front != Front.ReadingWalkthrough)
            Assert.Equal(readNewReply, tab.Queue.CurrentIndex >= tab.Queue.Count);
        AssertOneVoice();
    }

    // ---- Several chats at once ----

    [Theory]
    [MemberData(nameof(AllSettings))]
    public async Task Three_chats_replying_over_each_other_never_overlap(string autoRead, string whenPlaying)
    {
        _autoRead = autoRead;
        _whenPlaying = whenPlaying;

        Reply("a", "1@1", "A one. A two.");
        Reply("b", "2@1", "B one. B two.");
        Reply("c", "3@1", "C one. C two.");
        Reply("a", "1@1", "A three.");
        Reply("b", "2@1", "B three.");
        AssertOneVoice();

        await PlayToSilence();
        AssertOneVoice();

        // All chats with Wait reads every chat in turn; Off reads none.
        var everythingRead = _tabs.Tabs.All(t => t.Queue.CurrentIndex >= t.Queue.Count);
        if (autoRead == ReplyMode.All && whenPlaying == WhenPlaying.Wait) Assert.True(everythingRead);
        if (autoRead == ReplyMode.Off) Assert.All(_tabs.Tabs, t => Assert.Equal(0, t.Queue.CurrentIndex));
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public async Task Tool_calls_from_two_chats_never_overlap(string autoRead, string whenPlaying)
    {
        _autoRead = autoRead;
        _whenPlaying = whenPlaying;
        Reply(Chat1, Chat1Process, "Reply one.");
        Reply(Chat2, Chat2Process, "Reply two.");

        Tool("speak", Chat1Process, "A one. A two.");
        Tool("speak", Chat2Process, "B one. B two.");
        Tool("speak", Chat1Process, "A three.");
        AssertOneVoice();

        await PlayToSilence();
        AssertOneVoice();

        // With Wait nothing is cut off: each chat's request takes its turn and is read to the end,
        // whatever Auto Read says, since a chat asking to read something is not a reply.
        if (whenPlaying == WhenPlaying.Wait)
        {
            Assert.All(_tabs.Tabs, t => Assert.True(t.Queue.CurrentIndex >= t.Queue.Count, $"{t.Title} was not read to the end."));
            Assert.Contains("B two", Tab(Chat2).Html);
        }
    }

    /// <summary>
    /// Counts sentences that are playing at the same moment. Each one plays until Release() or
    /// until its reading is cancelled.
    /// </summary>
    private sealed class OverlapOutput : IAudioOutput
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource> _playing = [];

        public int MaxConcurrent { get; private set; }

        public bool Pending
        {
            get { lock (_lock) return _playing.Any(p => !p.Task.IsCompleted); }
        }

        public Task PlayAsync(AudioClip clip, CancellationToken ct)
        {
            lock (_lock)
            {
                _playing.RemoveAll(p => p.Task.IsCompleted);
                var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => playing.TrySetCanceled(ct));
                if (!playing.Task.IsCompleted) _playing.Add(playing);
                MaxConcurrent = Math.Max(MaxConcurrent, _playing.Count);
                return playing.Task;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                foreach (var playing in _playing) playing.TrySetResult();
                _playing.Clear();
            }
        }

        public void Pause() { }
        public void Resume() { }
    }
}
