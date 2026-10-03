using System.Diagnostics;
using MdReader.Bridge;
using MdReader.Core;

namespace MdReader.Tests;

public class ReplyHookTests
{
    private const string StopMessage = """
        {
          "session_id": "abc123",
          "transcript_path": "C:\\Users\\me\\.claude\\projects\\x\\t.jsonl",
          "cwd": "D:\\Projects\\thing",
          "permission_mode": "default",
          "hook_event_name": "Stop",
          "last_assistant_message": "I\u2019ve fixed it \u2014 two files changed.\n\nAll tests pass.",
          "stop_hook_active": true
        }
        """;

    private static string NewName() => $"MdReader.Test.{Guid.NewGuid():N}";

    [Fact]
    public void ExtractReply_returns_the_last_assistant_message_of_a_stop_event()
    {
        Assert.Equal(
            "I\u2019ve fixed it \u2014 two files changed.\n\nAll tests pass.",
            ReplyHook.ExtractReply(StopMessage));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("""{ "hook_event_name": "SubagentStop", "last_assistant_message": "x" }""")]
    [InlineData("""{ "hook_event_name": "PreToolUse", "last_assistant_message": "x" }""")]
    [InlineData("""{ "hook_event_name": 7, "last_assistant_message": "x" }""")]
    [InlineData("""{ "last_assistant_message": "x" }""")]
    [InlineData("""{ "hook_event_name": "Stop" }""")]
    [InlineData("""{ "hook_event_name": "Stop", "last_assistant_message": null }""")]
    [InlineData("""{ "hook_event_name": "Stop", "last_assistant_message": 42 }""")]
    [InlineData("""{ "hook_event_name": "Stop", "last_assistant_message": "   " }""")]
    public void ExtractReply_returns_null_for_anything_else(string input)
    {
        Assert.Null(ReplyHook.ExtractReply(input));
    }

    [Fact]
    public async Task Run_sends_the_reply_as_speak_reply()
    {
        PipeRequest? sent = null;
        var exit = await ReplyHook.RunAsync(new StringReader(StopMessage), request =>
        {
            sent = request;
            return Task.FromResult(PipeResponse.Success("ok"));
        });

        Assert.Equal(0, exit);
        Assert.Equal("speak_reply", sent!.Op);
        Assert.Equal("I\u2019ve fixed it \u2014 two files changed.\n\nAll tests pass.", sent.Text);
    }

    [Fact]
    public async Task Run_sends_nothing_for_input_that_is_not_a_reply()
    {
        var calls = 0;
        var exit = await ReplyHook.RunAsync(new StringReader("not json"), _ =>
        {
            calls++;
            return Task.FromResult(PipeResponse.Success("ok"));
        });

        Assert.Equal(0, exit);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_returns_zero_whatever_the_sender_throws()
    {
        foreach (var error in new Exception[]
                 {
                     new TimeoutException(), new IOException("pipe broke"), new InvalidOperationException("boom"),
                     new UnauthorizedAccessException(),
                 })
        {
            var exit = await ReplyHook.RunAsync(new StringReader(StopMessage), _ => throw error);
            Assert.Equal(0, exit);
        }
    }

    [Fact]
    public async Task Run_returns_zero_when_the_app_answers_with_an_error()
    {
        var exit = await ReplyHook.RunAsync(
            new StringReader(StopMessage), _ => Task.FromResult(PipeResponse.Fail("nope")));
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task The_reply_reaches_a_running_app_over_the_pipe()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Reading the reply (2 sentences)."));
        });
        server.Start();

        var exit = await ReplyHook.RunAsync(new StringReader(StopMessage), ReplyHook.SendTo(name));

        Assert.Equal(0, exit);
        Assert.Equal(("speak_reply", "I\u2019ve fixed it \u2014 two files changed.\n\nAll tests pass."), (seen!.Op, seen.Text));
    }

    [Fact]
    public async Task With_no_app_running_the_hook_gives_up_quickly()
    {
        var clock = Stopwatch.StartNew();
        var exit = await ReplyHook.RunAsync(new StringReader(StopMessage), ReplyHook.SendTo(NewName()));

        Assert.Equal(0, exit);
        Assert.True(clock.ElapsedMilliseconds < 3000, $"took {clock.ElapsedMilliseconds} ms");
    }
}
