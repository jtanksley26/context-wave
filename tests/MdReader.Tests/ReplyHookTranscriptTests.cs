using System.Text.Json;
using MdReader.Bridge;
using MdReader.Core;

namespace MdReader.Tests;

/// <summary>
/// Older versions of Claude Code do not put the reply in the Stop event; the hook then reads it
/// from the session transcript, a file of one JSON entry per line.
/// </summary>
public sealed class ReplyHookTranscriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdreader-transcript-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string User(string prompt) =>
        JsonSerializer.Serialize(new { type = "user", isSidechain = false, message = new { role = "user", content = prompt } });

    private static string ToolResult() =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            isSidechain = false,
            message = new { role = "user", content = new object[] { new { type = "tool_result", content = "ok" } } },
        });

    private static string Assistant(params object[] blocks) =>
        JsonSerializer.Serialize(new { type = "assistant", isSidechain = false, message = new { role = "assistant", content = blocks } });

    private static object TextBlock(string text) => new { type = "text", text };
    private static object Thinking() => new { type = "thinking", thinking = "hmm" };
    private static object ToolUse() => new { type = "tool_use", name = "Bash", input = new { command = "ls" } };

    private string StopEventFor(string transcriptPath) =>
        JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            session_id = "abc",
            transcript_path = transcriptPath,
            cwd = "D:\\work",
            stop_hook_active = false,
        });

    private string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void LastReply_is_the_text_after_the_last_user_entry()
    {
        var reply = ReplyHook.LastReply(
        [
            User("Fix the bug."),
            Assistant(Thinking()),
            Assistant(TextBlock("I'll look at the file.")),
            Assistant(ToolUse()),
            ToolResult(),
            Assistant(Thinking()),
            Assistant(TextBlock("Fixed it \u2014 one line changed.")),
        ]);

        Assert.Equal("Fixed it \u2014 one line changed.", reply);
    }

    [Fact]
    public void LastReply_joins_text_split_over_several_entries_and_blocks_in_order()
    {
        var reply = ReplyHook.LastReply(
        [
            User("Explain."),
            Assistant(TextBlock("First part."), TextBlock("Second part.")),
            Assistant(TextBlock("Third part.")),
        ]);

        Assert.Equal("First part.\n\nSecond part.\n\nThird part.", reply);
    }

    [Fact]
    public void LastReply_skips_other_entries_subagent_entries_and_lines_that_do_not_parse()
    {
        var subagent = JsonSerializer.Serialize(new
        {
            type = "assistant",
            isSidechain = true,
            message = new { role = "assistant", content = new[] { TextBlock("From a subagent.") } },
        });
        var reply = ReplyHook.LastReply(
        [
            "{\"type\":\"assis",
            User("Go."),
            Assistant(TextBlock("The answer.")),
            subagent,
            "{\"type\":\"last-prompt\"}",
            "{\"type\":\"system\",\"content\":\"note\"}",
            "",
            "not json at all",
            "[1, 2]",
            "{\"type\":\"assistant\",\"message\":{\"content\":\"a plain string\"}}",
            "{\"type\":\"assistant\",\"mess",
        ]);

        Assert.Equal("The answer.", reply);
    }

    [Fact]
    public void LastReply_is_null_when_no_text_follows_the_last_user_entry()
    {
        Assert.Null(ReplyHook.LastReply([Assistant(TextBlock("Earlier reply.")), User("Next question.")]));
        Assert.Null(ReplyHook.LastReply([User("Go."), Assistant(Thinking()), Assistant(ToolUse()), ToolResult()]));
        Assert.Null(ReplyHook.LastReply([User("Go."), Assistant(TextBlock("   "))]));
        Assert.Null(ReplyHook.LastReply([]));
    }

    [Fact]
    public void ExtractTranscriptPath_reads_the_path_from_a_stop_event_only()
    {
        Assert.Equal(@"C:\t\a.jsonl", ReplyHook.ExtractTranscriptPath(StopEventFor(@"C:\t\a.jsonl")));
        Assert.Null(ReplyHook.ExtractTranscriptPath("""{ "hook_event_name": "PreToolUse", "transcript_path": "x" }"""));
        Assert.Null(ReplyHook.ExtractTranscriptPath("""{ "hook_event_name": "Stop" }"""));
        Assert.Null(ReplyHook.ExtractTranscriptPath("not json"));
    }

    [Fact]
    public async Task Run_reads_the_reply_from_the_transcript_when_the_event_has_none()
    {
        var transcript = WriteTranscript(User("Hi."), Assistant(ToolUse()), ToolResult(), Assistant(TextBlock("All done.")));
        PipeRequest? sent = null;

        var exit = await ReplyHook.RunAsync(new StringReader(StopEventFor(transcript)), request =>
        {
            sent = request;
            return Task.FromResult(PipeResponse.Success("ok"));
        }, transcriptAttempts: 1, transcriptDelayMs: 0);

        Assert.Equal(0, exit);
        Assert.Equal(("speak_reply", "All done."), (sent!.Op, sent.Text));
    }

    [Fact]
    public async Task Run_prefers_the_reply_in_the_event_over_the_transcript()
    {
        var transcript = WriteTranscript(User("Hi."), Assistant(TextBlock("From the transcript.")));
        var input = JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            transcript_path = transcript,
            last_assistant_message = "From the event.",
        });
        PipeRequest? sent = null;

        await ReplyHook.RunAsync(new StringReader(input), request =>
        {
            sent = request;
            return Task.FromResult(PipeResponse.Success("ok"));
        }, transcriptAttempts: 1, transcriptDelayMs: 0);

        Assert.Equal("From the event.", sent!.Text);
    }

    [Fact]
    public async Task Run_waits_for_a_transcript_that_is_written_a_moment_late()
    {
        var transcript = WriteTranscript(User("Hi."));
        PipeRequest? sent = null;

        var running = ReplyHook.RunAsync(new StringReader(StopEventFor(transcript)), request =>
        {
            sent = request;
            return Task.FromResult(PipeResponse.Success("ok"));
        }, transcriptAttempts: 20, transcriptDelayMs: 50);
        await Task.Delay(120);
        File.AppendAllText(transcript, Assistant(TextBlock("Written late.")) + "\n");

        Assert.Equal(0, await running);
        Assert.Equal("Written late.", sent!.Text);
    }

    [Fact]
    public async Task Run_sends_nothing_when_the_transcript_is_missing_or_has_no_reply()
    {
        var calls = 0;
        Task<PipeResponse> Count(PipeRequest _)
        {
            calls++;
            return Task.FromResult(PipeResponse.Success("ok"));
        }

        var missing = Path.Combine(_dir, "missing.jsonl");
        Assert.Equal(0, await ReplyHook.RunAsync(new StringReader(StopEventFor(missing)), Count, 2, 10));
        var empty = WriteTranscript(User("Hi."));
        Assert.Equal(0, await ReplyHook.RunAsync(new StringReader(StopEventFor(empty)), Count, 2, 10));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_large_transcript_is_read_from_its_end()
    {
        var path = Path.Combine(_dir, "large.jsonl");
        var filler = Assistant(TextBlock(new string('x', 4000)));
        using (var writer = new StreamWriter(path))
        {
            writer.Write(User("Start.") + "\n");
            for (var i = 0; i < 1200; i++) writer.Write(filler + "\n");
            writer.Write(User("Last question.") + "\n");
            writer.Write(Assistant(TextBlock("The final answer.")) + "\n");
        }
        Assert.True(new FileInfo(path).Length > 4 * 1024 * 1024);

        Assert.Equal("The final answer.", await ReplyHook.ReplyFromTranscriptAsync(path, attempts: 1, delayMs: 0));
    }
}
