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
    public async Task Run_sends_the_chats_session_folder_and_processes_with_the_reply()
    {
        PipeRequest? sent = null;
        await ReplyHook.RunAsync(new StringReader(StopMessage), request =>
        {
            sent = request;
            return Task.FromResult(PipeResponse.Success("ok"));
        }, transcriptAttempts: 1, transcriptDelayMs: 0, processes: () => ["12@34", "56@78"]);

        Assert.Equal("abc123", sent!.Session);
        Assert.Equal(@"D:\Projects\thing", sent.Folder);
        Assert.Equal(new[] { "12@34", "56@78" }, sent.Processes);
    }

    [Fact]
    public void ProcessIdentity_names_this_process_parent()
    {
        var parent = ProcessIdentity.ParentKey();

        Assert.NotNull(parent);
        Assert.Matches(@"^\d+@\d+$", parent);
        Assert.Equal(ProcessIdentity.KeyOf(Environment.ProcessId), ProcessIdentity.KeyOf(Environment.ProcessId));
    }

    // The desktop app (1) runs each chat (10, 20) and also starts a bridge of its own (2). The hook
    // (99) runs under a shell (50) under chat 10, whose bridge is 11.
    private static readonly Dictionary<int, (int Parent, string Exe)> DesktopProcesses = new()
    {
        [1] = (0, "claude.exe"),
        [2] = (1, "MdReader.Bridge.exe"),
        [10] = (1, "claude.exe"),
        [11] = (10, "MdReader.Bridge.exe"),
        [20] = (1, "claude.exe"),
        [21] = (20, "MdReader.Bridge.exe"),
        [50] = (10, "bash.exe"),
        [99] = (50, "MdReader.Bridge.exe"),
    };

    [Fact]
    public void The_chat_process_is_the_nearest_ancestor_with_a_bridge_not_the_shared_app()
    {
        Assert.Equal(10, ProcessIdentity.ChatProcess([50, 10, 1], DesktopProcesses, 99, "mdreader.bridge.exe"));
    }

    [Fact]
    public void The_hook_itself_does_not_count_as_its_parent_s_bridge()
    {
        // Run straight from the chat, without a shell: the chat has the hook and its real bridge.
        var processes = new Dictionary<int, (int Parent, string Exe)>(DesktopProcesses) { [99] = (10, "MdReader.Bridge.exe") };
        Assert.Equal(10, ProcessIdentity.ChatProcess([10, 1], processes, 99, "MdReader.Bridge.exe"));
    }

    [Fact]
    public void A_chat_without_a_bridge_is_not_mistaken_for_the_app_above_it()
    {
        // Chat 10's bridge is not running; the app (1) above it still has one.
        var processes = new Dictionary<int, (int Parent, string Exe)>(DesktopProcesses);
        processes.Remove(11);

        Assert.Null(ProcessIdentity.ChatProcess([50, 10, 1], processes, 99, "MdReader.Bridge.exe"));
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

    private sealed class NeverEndingReader : TextReader
    {
        public override Task<string> ReadToEndAsync() => new TaskCompletionSource<string>().Task;
    }

    [Fact]
    public async Task Run_gives_up_when_standard_input_is_never_closed()
    {
        var calls = 0;
        var clock = Stopwatch.StartNew();
        var exit = await ReplyHook.RunAsync(new NeverEndingReader(), _ =>
        {
            calls++;
            return Task.FromResult(PipeResponse.Success("ok"));
        });

        Assert.Equal(0, exit);
        Assert.Equal(0, calls);
        Assert.True(clock.ElapsedMilliseconds < 5000, $"took {clock.ElapsedMilliseconds} ms");
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
