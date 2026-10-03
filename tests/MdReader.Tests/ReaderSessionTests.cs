using MdReader.Core;

namespace MdReader.Tests;

public sealed class ReaderSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdreader-session-").FullName;
    private readonly FakeOutput _output = new() { Manual = true };
    private readonly ReadingQueue _queue;
    private readonly ReaderSession _session;
    private readonly List<string> _replaced = [];
    private readonly List<string> _appended = [];
    private readonly List<(string Html, string? Title)> _diffs = [];
    private bool _voiceReady = true;
    private bool _announce = true;
    private string _replies = "switch";

    public ReaderSessionTests()
    {
        _queue = new ReadingQueue(
            new FakeTts(), _output, () => new VoiceSettings("m", 0, 1f), (_, _) => Task.CompletedTask);
        _session = new ReaderSession(_queue, () => _announce, () => _voiceReady, () => _replies);
        _session.DocumentReplaced += _replaced.Add;
        _session.DocumentAppended += _appended.Add;
        _session.DiffReplaced += (html, title) => _diffs.Add((html, title));
    }

    public void Dispose()
    {
        _queue.Stop();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private PipeResponse ReadFile(string path) => _session.Handle(new PipeRequest { Op = "read_file", Path = path });

    private PipeResponse Speak(string text, string? mode = null, string? focus = null) =>
        _session.Handle(new PipeRequest { Op = "speak", Text = text, Mode = mode, Focus = focus });

    private PipeResponse ShowDiff(string diff, string? title = null) =>
        _session.Handle(new PipeRequest { Op = "show_diff", Diff = diff, Title = title });

    private PipeResponse Reply(string text) =>
        _session.Handle(new PipeRequest { Op = "speak_reply", Text = text });

    /// <summary>Lets the sentence in progress finish and waits for the reader to go idle.</summary>
    private async Task FinishReading()
    {
        await TestUtil.WaitUntil(() =>
        {
            _output.Release();
            return _queue.State == ReadingState.Idle;
        });
    }

    private PipeResult Status() => _session.Handle(new PipeRequest { Op = "status" }).Result!;

    [Fact]
    public void ReadFile_loads_the_document_and_starts_reading()
    {
        var path = Write("a.md", "# T\n\nOne. Two.");
        var response = ReadFile(path);

        Assert.True(response.Ok, response.Error);
        Assert.Equal(path, _session.Source);
        Assert.Equal(3, _queue.Count);
        Assert.Equal(ReadingState.Playing, _queue.State);
        Assert.Contains("data-sid", Assert.Single(_replaced));
    }

    [Theory]
    [InlineData("notes.md", "absolute")]
    [InlineData(@"C:\definitely\missing\notes.md", "not found")]
    public void ReadFile_rejects_bad_paths(string path, string expected)
    {
        var response = ReadFile(path);
        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error);
    }

    [Fact]
    public void ReadFile_with_an_invalid_path_fails_cleanly()
    {
        var response = ReadFile("C:\\a\0.md");
        Assert.False(response.Ok);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void OpenFile_with_an_invalid_path_throws_ReaderException()
    {
        Assert.Throws<ReaderException>(() => _session.OpenFile("C:\\a\0.md"));
    }

    [Fact]
    public void ReadFile_on_a_directory_named_like_a_markdown_file_fails()
    {
        var folder = Path.Combine(_dir, "folder.md");
        Directory.CreateDirectory(folder);
        var response = ReadFile(folder);
        Assert.False(response.Ok);
        Assert.Contains("not found", response.Error);
    }

    [Fact]
    public void ReadFile_rejects_unsupported_extensions()
    {
        var response = ReadFile(Write("a.pdf", "x"));
        Assert.False(response.Ok);
        Assert.Contains("Unsupported", response.Error);
    }

    [Fact]
    public void ReadFile_rejects_files_over_the_limit()
    {
        var path = Path.Combine(_dir, "big.md");
        File.WriteAllBytes(path, new byte[ReaderSession.MaxFileBytes + 1]);
        var response = ReadFile(path);
        Assert.False(response.Ok);
        Assert.Contains("too large", response.Error);
    }

    [Fact]
    public void Failed_ReadFile_leaves_the_current_document_alone()
    {
        var path = Write("a.md", "One. Two.");
        ReadFile(path);
        ReadFile(Path.Combine(_dir, "missing.md"));

        Assert.Equal(path, _session.Source);
        Assert.Equal(2, _queue.Count);
        Assert.Single(_replaced);
    }

    [Fact]
    public void Tools_report_voice_not_ready()
    {
        _voiceReady = false;
        Assert.Contains("not ready", ReadFile(Write("a.md", "One.")).Error);
        Assert.Contains("not ready", Speak("Hello.").Error);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void OpenFile_from_the_ui_loads_without_playing_when_the_voice_is_missing()
    {
        _voiceReady = false;
        _session.OpenFile(Write("a.md", "One."));
        Assert.Equal(1, _queue.Count);
        Assert.Equal(ReadingState.Idle, _queue.State);
    }

    [Fact]
    public void Speak_starts_a_stream_and_then_appends()
    {
        Assert.True(Speak("First.").Ok);
        Assert.Equal("stream", _session.Source);
        Assert.Single(_replaced);

        Assert.True(Speak("Second.").Ok);
        Assert.Equal(2, _queue.Count);
        Assert.Contains("data-sid=\"1\"", Assert.Single(_appended));
    }

    [Fact]
    public void Speak_replace_starts_over()
    {
        Speak("First.");
        Speak("Second.");
        Speak("Third.", "replace");

        Assert.Equal(1, _queue.Count);
        Assert.Equal(2, _replaced.Count);
    }

    [Theory]
    [InlineData("Hello.", "shout", "mode")]
    [InlineData("   ", "append", "empty")]
    public void Speak_rejects_bad_input(string text, string mode, string expected)
    {
        var response = Speak(text, mode);
        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error);
    }

    [Fact]
    public void Stop_clears_the_document()
    {
        Speak("First.");
        Assert.True(_session.Handle(new PipeRequest { Op = "stop" }).Ok);

        Assert.Equal(0, _queue.Count);
        Assert.Equal("", _session.Source);
        Assert.Equal("", _replaced.Last());
    }

    [Fact]
    public void Status_reports_state_source_and_position()
    {
        Speak("One. Two.");
        var result = _session.Handle(new PipeRequest { Op = "status" }).Result!;

        Assert.Equal("playing", result.State);
        Assert.Equal("stream", result.Source);
        Assert.Equal(1, result.CurrentSentence);
        Assert.Equal(2, result.TotalSentences);
    }

    [Fact]
    public void Status_when_empty_is_idle_with_zero_position()
    {
        var result = _session.Handle(new PipeRequest { Op = "status" }).Result!;
        Assert.Equal(("idle", 0, 0), (result.State, result.CurrentSentence, result.TotalSentences));
    }

    [Fact]
    public void Activate_raises_the_event_and_unknown_ops_fail()
    {
        var activated = false;
        _session.ActivateRequested += () => activated = true;

        Assert.True(_session.Handle(new PipeRequest { Op = "activate" }).Ok);
        Assert.True(activated);
        Assert.Contains("Unknown", _session.Handle(new PipeRequest { Op = "dance" }).Error);
    }

    [Fact]
    public void ShowDiff_stops_reading_clears_the_document_and_shows_the_diff()
    {
        Speak("First.");
        var response = ShowDiff(SampleDiff.Many, " PR 12 ");

        Assert.True(response.Ok, response.Error);
        Assert.Equal("Showing diff: 5 files.", response.Result!.Message);
        Assert.Equal(0, _queue.Count);
        Assert.Equal(ReadingState.Idle, _queue.State);
        Assert.Equal("stream", _session.Source);
        Assert.Equal("", _replaced.Last());
        Assert.Equal("PR 12", _session.DiffTitle);
        var (html, title) = Assert.Single(_diffs);
        Assert.Contains("data-line=\"0\"", html);
        Assert.Equal("PR 12", title);
    }

    [Fact]
    public void ShowDiff_reports_a_single_file_in_the_singular()
    {
        Assert.Equal("Showing diff: 1 file.", ShowDiff(SampleDiff.Foo).Result!.Message);
        Assert.Null(_session.DiffTitle);
    }

    [Fact]
    public void ShowDiff_does_not_need_the_voice()
    {
        _voiceReady = false;
        Assert.True(ShowDiff(SampleDiff.Foo).Ok);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   \n", "empty")]
    [InlineData("hello there", "not a unified diff")]
    public void ShowDiff_rejects_bad_input_and_leaves_the_reading_alone(string diff, string expected)
    {
        Speak("First.");
        var response = ShowDiff(diff);

        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error);
        Assert.Equal(1, _queue.Count);
        Assert.Equal(ReadingState.Playing, _queue.State);
        Assert.Empty(_diffs);
        Assert.Single(_replaced);
    }

    [Fact]
    public void ShowDiff_rejects_a_diff_over_the_limit()
    {
        var response = ShowDiff(new string('x', ReaderSession.MaxDiffBytes + 1));
        Assert.False(response.Ok);
        Assert.Contains("too large", response.Error);
    }

    [Fact]
    public void Speak_after_ShowDiff_appends_and_starts_reading()
    {
        ShowDiff(SampleDiff.Foo);
        Assert.True(Speak("One.").Ok);

        Assert.Equal(1, _queue.Count);
        Assert.Equal(ReadingState.Playing, _queue.State);
        Assert.Contains("data-sid=\"0\"", Assert.Single(_appended));
    }

    [Fact]
    public void A_second_ShowDiff_replaces_the_first()
    {
        ShowDiff(SampleDiff.Many);
        ShowDiff(SampleDiff.Foo);

        Assert.Equal(2, _diffs.Count);
        Assert.Equal(1, Status().DiffFiles);
    }

    [Fact]
    public void Status_reports_the_number_of_files_in_the_diff()
    {
        Assert.Equal(0, Status().DiffFiles);
        ShowDiff(SampleDiff.Many);
        Assert.Equal(5, Status().DiffFiles);
    }

    [Fact]
    public void Stop_unloads_the_diff()
    {
        ShowDiff(SampleDiff.Foo, "PR 12");
        _session.Handle(new PipeRequest { Op = "stop" });

        Assert.Equal(("", (string?)null), _diffs.Last());
        Assert.Null(_session.DiffTitle);
        Assert.Equal(0, Status().DiffFiles);
    }

    [Fact]
    public void Stop_without_a_diff_does_not_raise_DiffReplaced()
    {
        Speak("First.");
        _session.Handle(new PipeRequest { Op = "stop" });
        Assert.Empty(_diffs);
    }

    [Fact]
    public void Opening_a_file_unloads_the_diff()
    {
        ShowDiff(SampleDiff.Foo);
        Assert.True(ReadFile(Write("a.md", "One.")).Ok);

        Assert.Equal(("", (string?)null), _diffs.Last());
        Assert.Equal(0, Status().DiffFiles);
    }

    [Fact]
    public void A_failed_ReadFile_keeps_the_diff()
    {
        ShowDiff(SampleDiff.Foo);
        ReadFile(Path.Combine(_dir, "missing.md"));

        Assert.Single(_diffs);
        Assert.Equal(1, Status().DiffFiles);
    }

    [Fact]
    public void Speak_with_focus_links_every_sentence_in_the_chunk_and_labels_it()
    {
        ShowDiff(SampleDiff.Foo);
        var response = Speak("One. Two.", focus: "Foo.cs:2-4");
        Speak("Three.");

        Assert.Equal("Queued 2 sentences.", response.Result!.Message);
        var expected = new DiffAnchor(0, 2, 5, "Foo.cs:2-4");
        Assert.Equal(expected, _session.AnchorFor(0));
        Assert.Equal(expected, _session.AnchorFor(1));
        Assert.Null(_session.AnchorFor(2));
        Assert.StartsWith("<div class=\"focus-label\">Foo.cs:2-4</div>", _appended[0]);
        Assert.DoesNotContain("focus-label", _appended[1]);
    }

    [Fact]
    public void Speak_with_a_focus_that_is_not_in_the_diff_still_reads_and_says_so()
    {
        ShowDiff(SampleDiff.Foo);
        var response = Speak("One.", focus: "Bar.cs:1");

        Assert.True(response.Ok);
        Assert.Equal("Queued 1 sentences. Focus 'Bar.cs:1' was not found in the diff.", response.Result!.Message);
        Assert.Equal(1, _queue.Count);
        Assert.Null(_session.AnchorFor(0));
        Assert.DoesNotContain("focus-label", _appended[0]);
    }

    [Fact]
    public void Speak_with_focus_but_no_diff_still_reads_and_says_so()
    {
        var response = Speak("One.", focus: "Foo.cs:1");

        Assert.True(response.Ok);
        Assert.Contains("no diff is loaded", response.Result!.Message);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public void Speak_replace_keeps_the_diff_and_drops_the_links()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        Speak("Again.", "replace", "Foo.cs:3");

        Assert.Single(_diffs);
        Assert.Equal(1, Status().DiffFiles);
        Assert.Equal(1, _queue.Count);
        Assert.Equal("Foo.cs:3", _session.AnchorFor(0)!.Label);
        Assert.StartsWith("<div class=\"focus-label\">Foo.cs:3</div>", _replaced.Last());
    }

    [Fact]
    public void Speak_replace_without_focus_drops_the_links()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        Speak("Again.", "replace");

        Assert.Null(_session.AnchorFor(0));
        Assert.Null(_session.SentenceForDiff(0, null));
        Assert.Equal(1, Status().DiffFiles);
    }

    [Fact]
    public void Stop_and_opening_a_file_drop_the_links()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        _session.Handle(new PipeRequest { Op = "stop" });
        Assert.Null(_session.AnchorFor(0));

        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        Assert.True(ReadFile(Write("a.md", "One.")).Ok);
        Assert.Null(_session.SentenceForDiff(0, null));
    }

    [Fact]
    public void Speak_with_focus_on_text_without_sentences_adds_no_label()
    {
        _announce = false;
        ShowDiff(SampleDiff.Foo);
        var response = Speak("~~~\ncode\n~~~", focus: "Foo.cs:2");

        Assert.True(response.Ok, response.Error);
        Assert.Equal(0, _queue.Count);
        Assert.DoesNotContain("focus-label", Assert.Single(_appended));
    }

    [Fact]
    public void A_new_diff_drops_the_links()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        ShowDiff(SampleDiff.Foo);

        Assert.Null(_session.AnchorFor(0));
        Assert.Null(_session.SentenceForDiff(0, null));
    }

    [Fact]
    public void SentenceForDiff_prefers_a_line_range_over_a_whole_file_link()
    {
        ShowDiff(SampleDiff.Many);
        Speak("Whole file.", focus: "src/Foo.cs");      // sentence 0, whole file
        Speak("The change.", focus: "src/Foo.cs:2-4");  // sentence 1, rows 2-5
        Speak("Once more.", focus: "src/Foo.cs:2");     // sentence 2, rows 2-3

        Assert.Equal(1, _session.SentenceForDiff(0, 3));
        Assert.Equal(1, _session.SentenceForDiff(0, 5));
        Assert.Equal(0, _session.SentenceForDiff(0, 9));
        Assert.Equal(0, _session.SentenceForDiff(0, null));
        Assert.Null(_session.SentenceForDiff(1, 12));
        Assert.Null(_session.SentenceForDiff(1, null));
    }

    [Fact]
    public void SentenceForDiff_ignores_lines_nobody_talked_about()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("Intro.");
        Speak("The change.", focus: "Foo.cs:2-4");      // sentence 1, rows 2-5

        Assert.Null(_session.SentenceForDiff(0, 9));
        Assert.Equal(1, _session.SentenceForDiff(0, 4));
        Assert.Equal(1, _session.SentenceForDiff(0, null));
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("queue")]
    [InlineData("finish")]
    public void A_reply_is_read_when_the_reader_is_free(string mode)
    {
        _replies = mode;
        var response = Reply("One. Two.");

        Assert.True(response.Ok, response.Error);
        Assert.Equal("Reading the reply (2 sentences).", response.Result!.Message);
        Assert.Equal("reply", _session.Source);
        Assert.Equal(2, _queue.Count);
        Assert.Equal(ReadingState.Playing, _queue.State);
        Assert.Contains("data-sid", Assert.Single(_replaced));
        Assert.Equal("reply", Status().Source);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("loud")]
    public void Replies_are_ignored_when_the_mode_is_off_or_unknown(string mode)
    {
        _replies = mode;
        var response = Reply("One.");

        Assert.True(response.Ok);
        Assert.Contains("off", response.Result!.Message);
        Assert.Equal(0, _queue.Count);
        Assert.Equal("", _session.Source);
        Assert.Empty(_replaced);
    }

    [Fact]
    public void Switch_replaces_the_reply_being_read()
    {
        Reply("One.");
        var response = Reply("Two. Three.");

        Assert.Equal("Reading the reply (2 sentences).", response.Result!.Message);
        Assert.Equal(2, _queue.Count);
        Assert.Equal(2, _replaced.Count);
        Assert.Empty(_appended);
    }

    [Fact]
    public void Queue_appends_after_the_reply_being_read()
    {
        _replies = "queue";
        Reply("One.");
        var response = Reply("Two.");

        Assert.Equal("Queued the reply (1 sentences) after the current one.", response.Result!.Message);
        Assert.Equal(2, _queue.Count);
        Assert.Single(_replaced);
        Assert.StartsWith("<hr>", Assert.Single(_appended));
        Assert.Contains("data-sid=\"1\"", _appended[0]);
    }

    [Fact]
    public void Queue_appends_while_the_reply_is_paused_and_stays_paused()
    {
        _replies = "queue";
        Reply("One.");
        _queue.Pause();
        Reply("Two.");

        Assert.Equal(2, _queue.Count);
        Assert.Equal(ReadingState.Paused, _queue.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finish_ignores_a_reply_while_one_is_playing_or_paused(bool paused)
    {
        _replies = "finish";
        Reply("One.");
        if (paused) _queue.Pause();
        var response = Reply("Two.");

        Assert.True(response.Ok);
        Assert.Contains("still being read", response.Result!.Message);
        Assert.Equal(1, _queue.Count);
        Assert.Single(_replaced);
        Assert.Empty(_appended);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("finish")]
    public async Task A_reply_that_has_finished_is_replaced_by_the_next(string mode)
    {
        _replies = mode;
        Reply("One.");
        await FinishReading();
        var response = Reply("Two. Three.");

        Assert.StartsWith("Reading the reply", response.Result!.Message);
        Assert.Equal(2, _queue.Count);
        Assert.Equal(2, _replaced.Count);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("queue")]
    public void A_reply_never_interrupts_text_claude_was_asked_to_speak(string mode)
    {
        _replies = mode;
        Speak("Hello there.");
        var response = Reply("Done.");

        Assert.True(response.Ok);
        Assert.Contains("something else", response.Result!.Message);
        Assert.Equal("stream", _session.Source);
        Assert.Equal(1, _queue.Count);
        Assert.Single(_replaced);
    }

    [Fact]
    public void A_reply_never_interrupts_a_file_even_when_it_is_paused()
    {
        var path = Write("a.md", "One. Two.");
        ReadFile(path);
        _queue.Pause();
        Reply("Done.");

        Assert.Equal(path, _session.Source);
        Assert.Equal(2, _queue.Count);
    }

    [Fact]
    public async Task A_reply_is_read_once_a_file_has_finished()
    {
        ReadFile(Write("a.md", "One."));
        await FinishReading();
        Reply("Done.");

        Assert.Equal("reply", _session.Source);
        Assert.Equal(2, _replaced.Count);
    }

    [Fact]
    public async Task A_reply_never_replaces_a_walkthrough_even_after_it_has_finished()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("About the change.", focus: "Foo.cs:2");
        await FinishReading();
        var response = Reply("Done.");

        Assert.Contains("walkthrough", response.Result!.Message);
        Assert.Equal("stream", _session.Source);
        Assert.Equal(1, Status().DiffFiles);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public void A_reply_is_skipped_without_an_error_when_the_voice_is_missing()
    {
        _voiceReady = false;
        var response = Reply("One.");

        Assert.True(response.Ok);
        Assert.Contains("voice", response.Result!.Message);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void Empty_and_oversized_replies_are_skipped()
    {
        Assert.Contains("empty", Reply("   ").Result!.Message);
        Assert.Contains("too long", Reply(new string('x', ReaderSession.MaxReplyChars + 1)).Result!.Message);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_replaced);
    }

    [Fact]
    public void Speak_after_a_reply_starts_a_new_document_instead_of_appending()
    {
        Reply("One.");
        Speak("Two.");

        Assert.Equal("stream", _session.Source);
        Assert.Equal(1, _queue.Count);
        Assert.Equal(2, _replaced.Count);
        Assert.Empty(_appended);
    }

    [Fact]
    public void ReadFile_replaces_a_reply()
    {
        Reply("One.");
        var path = Write("a.md", "Two. Three.");
        Assert.True(ReadFile(path).Ok);

        Assert.Equal(path, _session.Source);
        Assert.Equal(2, _queue.Count);
    }
}
