# Reading Claude's Replies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read each of Claude's replies aloud automatically through a Claude Code `Stop` hook, with a setting that chooses what happens when replies overlap.

**Architecture:** Claude Code runs `MdReader.Bridge.exe reply-hook` when a reply ends and passes the reply text on standard input. The Bridge forwards it to the running app as a new `speak_reply` pipe operation and always exits 0. `ReaderSession` decides, from the user's mode and what the reader is doing, whether to read, queue or skip it. `MdReader.Bridge.exe setup` installs the hook in `~/.claude/settings.json`.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-reply-hook-design.md`

## Notes for the implementer

- **Output folder.** Every dotnet command in this plan passes `-p:MdReaderOut=../../out-dev/`. Never build without it: `out/` is held open by the running app and by Claude sessions.
- Run commands from the repository root (`D:\Projects\md5reader`). They are written for Git Bash and work unchanged in PowerShell.
- Do not launch the app, and do not run `MdReader.Bridge.exe setup` from a task: it edits the user's real Claude settings. Setup is run at hand-over, with the user's agreement.
- A hook must never disturb Claude. `ReplyHook` prints nothing, catches everything and returns 0.
- Deviations from the spec, recorded in its "Amendments" section:
  - The hook entry uses the shell form, `"<path with forward slashes>/MdReader.Bridge.exe" reply-hook`, not `command` plus `args`. The shell form is the long-standing one and works in Git Bash and cmd.
  - The real hook message is not captured before the build. Instead, a `Stop` message without `last_assistant_message` is logged with its field names, so a wrong assumption shows up in the log at the manual check.
  - The Bridge reads standard input as UTF-8 explicitly; the console default would garble quotes and dashes.
  - A reply queued behind another is separated from it by a horizontal rule.
  - Setup skips the hook when `~/.claude` does not exist (Claude Code is not installed) rather than creating the folder.

## File map

```
src/MdReader.Core/
  ReplyMode.cs           (new)   the four modes
  Settings.cs                    Replies value
  ReaderSession.cs               speak_reply, the "reply" source
src/MdReader.Bridge/
  ReplyHook.cs           (new)   reply-hook mode
  ClaudeSettings.cs      (new)   add or remove the hook in settings.json text
  Program.cs                     reply-hook argument
  SetupCommand.cs                install or remove the hook
src/MdReader.App/
  MainWindow.xaml                Claude's replies submenu
  MainWindow.xaml.cs             menu, title, mode callback
tests/MdReader.Tests/
  ReplyModeTests.cs      (new)
  ReplyHookTests.cs      (new)
  ClaudeSettingsTests.cs (new)
  ReaderSessionTests.cs          new tests
  SettingsTests.cs               one new test
README.md
```

---

### Task 1: Reply modes and the setting

**Files:**
- Create: `src/MdReader.Core/ReplyMode.cs`
- Modify: `src/MdReader.Core/Settings.cs`
- Create: `tests/MdReader.Tests/ReplyModeTests.cs`
- Modify: `tests/MdReader.Tests/SettingsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/ReplyModeTests.cs`:

```csharp
using MdReader.Core;

namespace MdReader.Tests;

public class ReplyModeTests
{
    [Fact]
    public void Choices_are_the_four_modes_in_menu_order()
    {
        Assert.Equal(new[] { "off", "switch", "queue", "finish" }, ReplyMode.Choices.Select(c => c.Id));
        Assert.Equal(
            new[] { "Off", "Switch to the newest", "Queue", "Finish the current one" },
            ReplyMode.Choices.Select(c => c.DisplayName));
        Assert.Equal(
            ("off", "switch", "queue", "finish"),
            (ReplyMode.Off, ReplyMode.Switch, ReplyMode.Queue, ReplyMode.Finish));
    }

    [Theory]
    [InlineData("queue", "queue")]
    [InlineData("off", "off")]
    [InlineData(null, "off")]
    [InlineData("", "off")]
    [InlineData("loud", "off")]
    public void Normalize_keeps_known_modes_and_turns_the_rest_off(string? id, string expected)
    {
        Assert.Equal(expected, ReplyMode.Normalize(id));
    }
}
```

Add to `SettingsTests`, before the closing brace of the class:

```csharp
    [Fact]
    public void Replies_defaults_to_off_round_trips_and_falls_back_when_blank()
    {
        Assert.Equal("off", Settings.Load(File_).Replies);

        new Settings { Replies = "queue" }.Save(File_);
        Assert.Equal("queue", Settings.Load(File_).Replies);

        File.WriteAllText(File_, "{\"Replies\":null}");
        Assert.Equal("off", Settings.Load(File_).Replies);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReplyModeTests|FullyQualifiedName~SettingsTests"`
Expected: build fails with `error CS0103: The name 'ReplyMode' does not exist in the current context`.

- [ ] **Step 3: Write the modes and the setting**

Create `src/MdReader.Core/ReplyMode.cs`:

```csharp
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
```

In `src/MdReader.Core/Settings.cs` add a property after `Visualizer`:

```csharp
    public string Replies { get; set; } = ReplyMode.Off;
```

In `Load`, after the line that defaults `loaded.Visualizer`, add:

```csharp
                if (string.IsNullOrEmpty(loaded.Replies)) loaded.Replies = ReplyMode.Off;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReplyModeTests|FullyQualifiedName~SettingsTests"`
Expected: PASS, 16 test cases (6 in ReplyModeTests, 10 in SettingsTests).

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/ReplyMode.cs src/MdReader.Core/Settings.cs tests/MdReader.Tests/ReplyModeTests.cs tests/MdReader.Tests/SettingsTests.cs
git commit -m "feat(core): add the reply modes and their setting"
```

---

### Task 2: The speak_reply operation

**Files:**
- Modify: `src/MdReader.Core/ReaderSession.cs`
- Modify: `tests/MdReader.Tests/ReaderSessionTests.cs`

- [ ] **Step 1: Write the failing tests**

In `tests/MdReader.Tests/ReaderSessionTests.cs`:

Add a field after `_announce`:

```csharp
    private string _replies = "switch";
```

Change the line that constructs the session to pass the mode:

```csharp
        _session = new ReaderSession(_queue, () => _announce, () => _voiceReady, () => _replies);
```

Add a helper after `ShowDiff`:

```csharp
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
```

Add these tests at the end of the class:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderSessionTests"`
Expected: build fails with `error CS1729: 'ReaderSession' does not contain a constructor that takes 4 arguments`.

- [ ] **Step 3: Implement speak_reply**

In `src/MdReader.Core/ReaderSession.cs`:

Change the class declaration to take the mode:

```csharp
public sealed class ReaderSession(
    ReadingQueue queue, Func<bool> announceCodeBlocks, Func<bool> voiceReady, Func<string>? replyMode = null)
```

Add constants below `MaxDiffBytes`:

```csharp
    public const int MaxReplyChars = 200_000;

    /// <summary>The <see cref="Source"/> while one of Claude's replies is the current document.</summary>
    public const string ReplySource = "reply";
```

Change the summary on `Source` to:

```csharp
    /// <summary>
    /// "" when empty, "stream" for text Claude sent with speak, "reply" for a reply read
    /// automatically, otherwise the file path.
    /// </summary>
```

Add a case to the `switch` in `Handle`, after the `show_diff` case:

```csharp
                case "speak_reply":
                    // Always a success: a skipped reply is normal, and the hook must not see an error.
                    return PipeResponse.Success(SpeakReply(request.Text ?? ""));
```

In `Speak`, replace

```csharp
        var replace = mode == "replace" || Source == "";
```

with

```csharp
        // Text Claude was asked to speak never continues a reply that was read automatically.
        var replace = mode == "replace" || Source == "" || Source == ReplySource;
```

Add this method after `Speak`:

```csharp
    /// <summary>
    /// Reads one of Claude's replies if the mode and the reader's state allow it. A file, a speak
    /// stream or a walkthrough is never interrupted; only another reply can be replaced or queued behind.
    /// </summary>
    /// <returns>What happened, for the caller's log.</returns>
    public string SpeakReply(string text)
    {
        var mode = ReplyMode.Normalize(replyMode?.Invoke());
        if (mode == ReplyMode.Off) return "Skipped: reading replies is off.";
        if (string.IsNullOrWhiteSpace(text)) return "Skipped: the reply is empty.";
        if (text.Length > MaxReplyChars) return "Skipped: the reply is too long.";
        if (!voiceReady()) return "Skipped: the voice is not ready.";
        if (_diff is not null) return "Skipped: a walkthrough is showing.";

        var busy = queue.State != ReadingState.Idle;
        if (busy && Source != ReplySource) return "Skipped: something else is being read.";
        if (busy && mode == ReplyMode.Finish) return "Skipped: a reply is still being read.";

        if (busy && mode == ReplyMode.Queue)
        {
            var added = _document.Append(text);
            DocumentAppended?.Invoke($"<hr>{added.Html}");
            queue.Append(added.Sentences);
            return $"Queued the reply ({added.Sentences.Count} sentences) after the current one.";
        }

        _document = new MarkdownDocument(announceCodeBlocks());
        _anchors.Clear();
        var result = _document.Append(text);
        Source = ReplySource;
        DocumentReplaced?.Invoke(result.Html);
        queue.Load(result.Sentences);
        return $"Reading the reply ({result.Sentences.Count} sentences).";
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderSessionTests"`
Expected: PASS, including every test that existed before this task.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/ReaderSession.cs tests/MdReader.Tests/ReaderSessionTests.cs
git commit -m "feat(core): add the speak_reply operation"
```

---

### Task 3: The hook command

**Files:**
- Create: `src/MdReader.Bridge/ReplyHook.cs`
- Modify: `src/MdReader.Bridge/Program.cs`
- Create: `tests/MdReader.Tests/ReplyHookTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/ReplyHookTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReplyHookTests"`
Expected: build fails with `error CS0103: The name 'ReplyHook' does not exist in the current context`.

- [ ] **Step 3: Write the hook**

Create `src/MdReader.Bridge/ReplyHook.cs`:

```csharp
using System.Text.Json;
using MdReader.Core;

namespace MdReader.Bridge;

/// <summary>
/// The command Claude Code runs when a reply ends ("MdReader.Bridge.exe reply-hook"). It forwards
/// the reply to a running MD Reader. A hook must never disturb Claude, so this prints nothing,
/// swallows every failure and always returns 0.
/// </summary>
public static class ReplyHook
{
    private const int ConnectTimeoutMs = 300;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(TextReader input, Func<PipeRequest, Task<PipeResponse>> send)
    {
        try
        {
            var reply = ExtractReply(await input.ReadToEndAsync());
            if (reply is not null) await send(new PipeRequest { Op = "speak_reply", Text = reply });
        }
        catch (Exception)
        {
            // Not running, pipe trouble, anything: reading a reply aloud is never worth failing a hook.
        }
        return 0;
    }

    /// <summary>The reply text from a Stop event's JSON, or null when there is nothing to read.</summary>
    public static string? ExtractReply(string hookInput)
    {
        if (string.IsNullOrWhiteSpace(hookInput)) return null;
        try
        {
            using var document = JsonDocument.Parse(hookInput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("hook_event_name", out var name)
                || name.ValueKind != JsonValueKind.String || name.GetString() != "Stop")
                return null;

            if (!root.TryGetProperty("last_assistant_message", out var message)
                || message.ValueKind != JsonValueKind.String)
            {
                // The one assumption this feature rests on; say so if Claude Code sends something else.
                var fields = string.Join(", ", root.EnumerateObject().Select(p => p.Name));
                FileLog.Write($"reply-hook: the Stop event had no last_assistant_message text (fields: {fields}).");
                return null;
            }

            var text = message.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends to an app that is already running; never starts one.</summary>
    public static Func<PipeRequest, Task<PipeResponse>> SendTo(string pipeName) => async request =>
    {
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        return await PipeClient.SendAsync(pipeName, request, ConnectTimeoutMs, timeout.Token);
    };
}
```

- [ ] **Step 4: Add the command-line mode**

In `src/MdReader.Bridge/Program.cs`, add `using MdReader.Core;` to the usings at the top, and replace the block

```csharp
if (args.Length > 0)
{
    // Both usage paths return before any configuration code runs.
    if (args[0] != "setup")
    {
        Console.Error.WriteLine(
            "MdReader.Bridge is an MCP server started by Claude. To register it: MdReader.Bridge.exe setup [--remove]");
        return 2;
    }
```

with

```csharp
if (args.Length > 0)
{
    if (args[0] == "reply-hook")
    {
        // Claude Code sends UTF-8; the console's own input encoding would garble quotes and dashes.
        using var input = new StreamReader(Console.OpenStandardInput(), PipeProtocol.Utf8);
        return await ReplyHook.RunAsync(input, ReplyHook.SendTo(PipeProtocol.DefaultPipeName));
    }

    // Both usage paths return before any configuration code runs.
    if (args[0] != "setup")
    {
        Console.Error.WriteLine(
            "MdReader.Bridge is an MCP server started by Claude. To register it: MdReader.Bridge.exe setup [--remove]");
        return 2;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReplyHookTests"`
Expected: PASS, 21 test cases.

- [ ] **Step 6: Commit**

```bash
git add src/MdReader.Bridge/ReplyHook.cs src/MdReader.Bridge/Program.cs tests/MdReader.Tests/ReplyHookTests.cs
git commit -m "feat(bridge): add the reply-hook command"
```

---

### Task 4: Installing the hook

**Files:**
- Create: `src/MdReader.Bridge/ClaudeSettings.cs`
- Modify: `src/MdReader.Bridge/SetupCommand.cs`
- Create: `tests/MdReader.Tests/ClaudeSettingsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/ClaudeSettingsTests.cs`:

```csharp
using System.Text.Json.Nodes;
using MdReader.Bridge;

namespace MdReader.Tests;

public class ClaudeSettingsTests
{
    private const string Exe = @"C:\Apps\MdReader\MdReader.Bridge.exe";
    private const string Command = "\"C:/Apps/MdReader/MdReader.Bridge.exe\" reply-hook";

    private static JsonArray StopGroups(string json) => JsonNode.Parse(json)!["hooks"]!["Stop"]!.AsArray();

    [Fact]
    public void Creates_the_hook_when_there_is_no_file()
    {
        var json = ClaudeSettings.Apply(null, Exe, remove: false);
        var hook = Assert.Single(Assert.Single(StopGroups(json))!["hooks"]!.AsArray())!;

        Assert.Equal("command", (string)hook["type"]!);
        Assert.Equal(Command, (string)hook["command"]!);
        Assert.Equal(10, (int)hook["timeout"]!);
        Assert.True((bool)hook["async"]!);
        Assert.True(ClaudeSettings.HasHook(json));
    }

    [Fact]
    public void Preserves_other_settings_and_other_hooks()
    {
        const string existing = """
            {
              "enabledPlugins": { "a": true },
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "check.sh" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "notify.sh" } ] } ]
              }
            }
            """;
        var root = JsonNode.Parse(ClaudeSettings.Apply(existing, Exe, remove: false))!;

        Assert.True((bool)root["enabledPlugins"]!["a"]!);
        Assert.Equal("check.sh", (string)root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!);
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Equal("notify.sh", (string)stop[0]!["hooks"]![0]!["command"]!);
        Assert.Equal(Command, (string)stop[1]!["hooks"]![0]!["command"]!);
    }

    [Fact]
    public void Adding_again_updates_the_path_without_duplicating()
    {
        var first = ClaudeSettings.Apply(null, @"D:\old\MdReader.Bridge.exe", remove: false);
        var second = ClaudeSettings.Apply(first, Exe, remove: false);

        var hook = Assert.Single(Assert.Single(StopGroups(second))!["hooks"]!.AsArray())!;
        Assert.Equal(Command, (string)hook["command"]!);
    }

    [Fact]
    public void Remove_takes_out_only_our_hook_and_cleans_up_empty_containers()
    {
        var added = ClaudeSettings.Apply("""{ "theme": "dark" }""", Exe, remove: false);
        var removed = JsonNode.Parse(ClaudeSettings.Apply(added, Exe, remove: true))!.AsObject();

        Assert.Equal("dark", (string)removed["theme"]!);
        Assert.False(removed.ContainsKey("hooks"));
        Assert.False(ClaudeSettings.HasHook(removed.ToJsonString()));
    }

    [Fact]
    public void Remove_keeps_other_stop_hooks_including_one_in_the_same_group()
    {
        var existing = $$"""
            {
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "notify.sh" } ] },
                  { "hooks": [ { "type": "command", "command": "log.sh" },
                               { "type": "command", "command": "\"D:/x/MdReader.Bridge.exe\" reply-hook" } ] }
                ]
              }
            }
            """;
        var stop = StopGroups(ClaudeSettings.Apply(existing, Exe, remove: true));

        Assert.Equal(2, stop.Count);
        Assert.Equal("notify.sh", (string)stop[0]!["hooks"]![0]!["command"]!);
        Assert.Equal("log.sh", (string)Assert.Single(stop[1]!["hooks"]!.AsArray())!["command"]!);
    }

    [Fact]
    public void Recognises_the_hook_written_with_command_and_args()
    {
        const string existing = """
            { "hooks": { "Stop": [ { "hooks": [
              { "type": "command", "command": "C:\\Apps\\MdReader\\MdReader.Bridge.exe", "args": ["reply-hook"] } ] } ] } }
            """;
        Assert.True(ClaudeSettings.HasHook(existing));
        Assert.False(JsonNode.Parse(ClaudeSettings.Apply(existing, Exe, remove: true))!.AsObject().ContainsKey("hooks"));
    }

    [Fact]
    public void Remove_on_a_file_without_the_hook_changes_nothing_that_matters()
    {
        const string existing = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify.sh" } ] } ] } }""";
        var result = ClaudeSettings.Apply(existing, Exe, remove: true);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(existing), JsonNode.Parse(result)));
        Assert.False(ClaudeSettings.HasHook(existing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{ "hooks": "oops" }""")]
    public void HasHook_is_false_for_missing_or_malformed_settings(string? json)
    {
        Assert.False(ClaudeSettings.HasHook(json));
    }

    [Theory]
    [InlineData("[1, 2]", "settings.json is not a JSON object.")]
    [InlineData("""{ "hooks": ["a"] }""", "settings.json has an unexpected 'hooks' value.")]
    [InlineData("""{ "hooks": { "Stop": { "x": 1 } } }""", "settings.json has an unexpected 'hooks.Stop' value.")]
    public void Rejects_shapes_it_does_not_understand(string existing, string message)
    {
        foreach (var remove in new[] { false, true })
        {
            var ex = Assert.Throws<InvalidDataException>(() => ClaudeSettings.Apply(existing, Exe, remove));
            Assert.Equal(message, ex.Message);
        }
    }

    [Fact]
    public void Entries_of_an_unexpected_shape_inside_stop_are_left_alone()
    {
        const string existing = """{ "hooks": { "Stop": [ "text", { "hooks": "nope" }, { "matcher": "x" } ] } }""";
        var stop = StopGroups(ClaudeSettings.Apply(existing, Exe, remove: false));

        Assert.Equal(4, stop.Count);
        Assert.Equal("text", (string)stop[0]!);
        Assert.Equal(Command, (string)stop[3]!["hooks"]![0]!["command"]!);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ClaudeSettingsTests"`
Expected: build fails with `error CS0103: The name 'ClaudeSettings' does not exist in the current context`.

- [ ] **Step 3: Write ClaudeSettings**

Create `src/MdReader.Bridge/ClaudeSettings.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MdReader.Bridge;

/// <summary>Adds or removes MD Reader's Stop hook in the text of Claude Code's settings.json.</summary>
public static class ClaudeSettings
{
    public const string HookArgument = "reply-hook";

    private const string BridgeExeName = "MdReader.Bridge.exe";

    /// <summary>True when the settings contain MD Reader's Stop hook.</summary>
    public static bool HasHook(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return false;
        try
        {
            return JsonNode.Parse(existingJson) is JsonObject root
                   && root["hooks"] is JsonObject hooks
                   && hooks["Stop"] is JsonArray stop
                   && stop.Any(group => InnerHooks(group)?.Any(IsOurs) == true);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns the updated contents of settings.json. Everything else in it is kept.</summary>
    public static string Apply(string? existingJson, string bridgeExePath, bool remove)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject
              ?? throw new InvalidDataException("settings.json is not a JSON object.");

        var hooksNode = root["hooks"];
        if (hooksNode is not null and not JsonObject)
            throw new InvalidDataException("settings.json has an unexpected 'hooks' value.");
        var hooks = hooksNode as JsonObject;

        var stopNode = hooks?["Stop"];
        if (stopNode is not null and not JsonArray)
            throw new InvalidDataException("settings.json has an unexpected 'hooks.Stop' value.");
        var stop = stopNode as JsonArray;

        // Take out any entry of ours first, so adding again updates the path instead of duplicating.
        if (stop is not null)
        {
            for (var i = stop.Count - 1; i >= 0; i--)
            {
                if (InnerHooks(stop[i]) is not { } inner) continue;
                var had = inner.Count;
                for (var j = inner.Count - 1; j >= 0; j--)
                    if (IsOurs(inner[j])) inner.RemoveAt(j);
                if (had > 0 && inner.Count == 0) stop.RemoveAt(i);
            }
        }

        if (!remove)
        {
            if (hooks is null)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }
            if (stop is null)
            {
                stop = new JsonArray();
                hooks["Stop"] = stop;
            }
            stop.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    // Forward slashes and quotes work in both Git Bash and cmd, whichever runs the hook.
                    ["command"] = $"\"{bridgeExePath.Replace('\\', '/')}\" {HookArgument}",
                    ["timeout"] = 10,
                    ["async"] = true,
                }),
            });
        }

        if (stop is { Count: 0 }) hooks!.Remove("Stop");
        if (hooks is { Count: 0 }) root.Remove("hooks");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The "hooks" array of one entry of an event's list, or null when it has another shape.</summary>
    private static JsonArray? InnerHooks(JsonNode? group) => (group as JsonObject)?["hooks"] as JsonArray;

    /// <summary>Our hook is the one that runs the Bridge with "reply-hook", in either form.</summary>
    private static bool IsOurs(JsonNode? hook)
    {
        if (hook is not JsonObject entry) return false;
        if (entry["command"] is not JsonValue value || !value.TryGetValue<string>(out var command)) return false;
        if (!command.Contains(BridgeExeName, StringComparison.OrdinalIgnoreCase)) return false;
        if (command.Contains(HookArgument, StringComparison.Ordinal)) return true;
        return entry["args"] is JsonArray args
               && args.Any(a => a is JsonValue v && v.TryGetValue<string>(out var text) && text == HookArgument);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ClaudeSettingsTests"`
Expected: PASS, 16 test cases.

- [ ] **Step 5: Install and remove the hook from setup**

In `src/MdReader.Bridge/SetupCommand.cs`, in `Run`, directly above the line

```csharp
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
```

add

```csharp
        if (ApplyReplyHook(exe, remove) != 0) exitCode = 1;

```

Add this method directly above the `RunCmd` method's `/// <summary>` comment:

```csharp
    /// <summary>Adds or removes the Stop hook that reads Claude's replies aloud.</summary>
    private static int ApplyReplyHook(string exe, bool remove)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("Claude Code replies hook: no .claude folder; skipped.");
            return 0;
        }

        var file = Path.Combine(directory, "settings.json");
        try
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (remove && !ClaudeSettings.HasHook(existing))
            {
                Console.WriteLine("Claude Code replies hook: was not installed.");
                return 0;
            }

            var updated = ClaudeSettings.Apply(existing, exe, remove);
            if (!string.IsNullOrWhiteSpace(existing)
                && System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(existing), System.Text.Json.Nodes.JsonNode.Parse(updated)))
            {
                Console.WriteLine("Claude Code replies hook: already installed.");
                return 0;
            }

            if (existing is not null)
            {
                var backup = $"{file}.bak-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(file, backup);
                Console.WriteLine($"Claude Code replies hook: backup saved to {backup}");
            }
            File.WriteAllText(file, updated);
            Console.WriteLine(remove
                ? "Claude Code replies hook: removed. Start a new Claude Code session to apply."
                : "Claude Code replies hook: installed. Start a new Claude Code session to apply, " +
                  "then choose a mode under Settings > Claude's replies in MD Reader.");
            return 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Claude Code replies hook: settings left unchanged ({ex.Message}).");
            return 1;
        }
    }

```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 7: Commit**

```bash
git add src/MdReader.Bridge/ClaudeSettings.cs src/MdReader.Bridge/SetupCommand.cs tests/MdReader.Tests/ClaudeSettingsTests.cs
git commit -m "feat(bridge): install the replies hook from setup"
```

---

### Task 5: Menu and title

**Files:**
- Modify: `src/MdReader.App/MainWindow.xaml`
- Modify: `src/MdReader.App/MainWindow.xaml.cs`

- [ ] **Step 1: Add the submenu**

In `src/MdReader.App/MainWindow.xaml`, directly below the line

```xml
                <MenuItem x:Name="VisualizerMenu" Header="_Visualiser" />
```

add

```xml
                <Separator />
                <MenuItem x:Name="RepliesMenu" Header="Claude's _replies" />
```

- [ ] **Step 2: Wire the mode, the menu and the title**

In `src/MdReader.App/MainWindow.xaml.cs`:

Replace the line

```csharp
        _session = new ReaderSession(_queue, () => _settings.AnnounceCodeBlocks, VoiceReady);
```

with

```csharp
        _session = new ReaderSession(
            _queue, () => _settings.AnnounceCodeBlocks, VoiceReady, () => _settings.Replies);
```

In the constructor, directly below the line `ApplyVisualizer();`, add:

```csharp
        BuildRepliesMenu();
```

In `UpdateTitle`, add a case below the `"stream"` line:

```csharp
            ReaderSession.ReplySource => "Claude's reply - MD Reader",
```

Add these methods directly above `private void BuildVisualizerMenu()`:

```csharp
    private void BuildRepliesMenu()
    {
        foreach (var (id, name) in ReplyMode.Choices)
        {
            var item = new MenuItem { Header = name, Tag = id, IsCheckable = true };
            item.Click += OnRepliesClick;
            RepliesMenu.Items.Add(item);
        }
        TickReplies();
    }

    /// <summary>Clicking a checkable item toggles it first, so set every tick from the setting.</summary>
    private void TickReplies()
    {
        var mode = ReplyMode.Normalize(_settings.Replies);
        foreach (MenuItem item in RepliesMenu.Items) item.IsChecked = (string)item.Tag == mode;
    }

    private void OnRepliesClick(object sender, RoutedEventArgs e)
    {
        _settings.Replies = (string)((MenuItem)sender).Tag;
        SaveSettings();
        TickReplies();
    }

```

- [ ] **Step 3: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors and 0 warnings.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.App/MainWindow.xaml src/MdReader.App/MainWindow.xaml.cs
git commit -m "feat(app): choose how Claude's replies are read from the Settings menu"
```

---

### Task 6: Documentation and hand-over

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the README**

In `README.md`, add a new section directly above `## Environment`:

```markdown
## Reading Claude's replies

`setup` also installs a Claude Code hook that passes each finished reply to MD Reader. Choose
what happens under **Settings > Claude's replies**:

- **Off** (the default): replies are not read.
- **Switch to the newest**: a new reply replaces the one being read.
- **Queue**: a new reply is read after the current one.
- **Finish the current one**: a new reply is ignored while one is being read.

A reply never interrupts a file, text Claude was asked to speak, or a diff walkthrough, and
nothing is read unless MD Reader is already open. The hook applies to Claude Code sessions
(the terminal and the desktop app's Code tab) started after `setup` was run.
```

- [ ] **Step 2: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: describe reading Claude's replies"
```

- [ ] **Step 4: Hand over**

This needs the real build, a change to the user's Claude settings, and a new Claude Code session, so it is done with the user:

1. With the user's agreement, close MD Reader and rebuild into `out/` (`dotnet build MdReader.sln`).
2. With the user's agreement, run `./out/MdReader.Bridge.exe setup`. It prints what it changed and where it saved the backup of `~/.claude/settings.json`. Show the user the hook entry it added.
3. The user starts MD Reader, picks **Settings > Claude's replies > Switch to the newest**, and starts a new Claude Code session.
4. Check together:
   - a reply in the new session is read aloud when it finishes, and the title bar says "Claude's reply";
   - each of the other three modes behaves as described when a second reply arrives during the first;
   - a reply is not read while a file or a diff walkthrough is being read;
   - with MD Reader closed, a reply does not start it;
   - Claude's turn is not delayed or blocked.
5. If nothing is read, look in `%LOCALAPPDATA%\MdReader\logs` for a `reply-hook:` line. It lists the fields Claude Code actually sent, which shows whether `last_assistant_message` has another name in this version.
