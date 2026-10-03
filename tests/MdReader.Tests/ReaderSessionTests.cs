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

    public ReaderSessionTests()
    {
        _queue = new ReadingQueue(
            new FakeTts(), _output, () => new VoiceSettings("m", 0, 1f), (_, _) => Task.CompletedTask);
        _session = new ReaderSession(_queue, () => true, () => _voiceReady);
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

    private PipeResponse Speak(string text, string? mode = null) =>
        _session.Handle(new PipeRequest { Op = "speak", Text = text, Mode = mode });

    private PipeResponse ShowDiff(string diff, string? title = null) =>
        _session.Handle(new PipeRequest { Op = "show_diff", Diff = diff, Title = title });

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
}
