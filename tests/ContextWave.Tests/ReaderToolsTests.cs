using ContextWave.Bridge;
using ContextWave.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ContextWave.Tests;

public class ReaderToolsTests
{
    private const string ChatKey = "42@7";

    /// <summary>Runs one tool call against a fake app that answers <paramref name="reply"/>, and returns what it was sent.</summary>
    private static async Task<(string Text, PipeRequest Seen)> Call(
        Func<ReaderTools, Task<string>> call, PipeResponse reply)
    {
        var name = $"ContextWave.Test.{Guid.NewGuid():N}";
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(reply);
        });
        server.Start();

        var tools = new ReaderTools(new AppLink(name, () => false, TimeSpan.FromSeconds(2)), () => ChatKey);
        var text = await call(tools);
        return (text, seen!);
    }

    [Fact]
    public void The_host_can_create_the_tools_from_the_link_alone()
    {
        // The optional process-key parameter must not stop dependency injection building the tools in production.
        var services = new ServiceCollection()
            .AddSingleton(new AppLink("unused", () => false, TimeSpan.FromSeconds(1)))
            .BuildServiceProvider();

        Assert.NotNull(ActivatorUtilities.CreateInstance<ReaderTools>(services));
    }

    [Fact]
    public async Task ShowDiff_sends_the_diff_and_title()
    {
        var (text, seen) = await Call(
            tools => tools.ShowDiff(SampleDiff.Foo, "PR 12", CancellationToken.None),
            PipeResponse.Success("Showing diff: 1 file."));

        Assert.Equal("Showing diff: 1 file.", text);
        Assert.Equal(("show_diff", SampleDiff.Foo, "PR 12"), (seen.Op, seen.Diff, seen.Title));
    }

    [Fact]
    public async Task Speak_sends_the_focus()
    {
        var (_, seen) = await Call(
            tools => tools.Speak("Hello.", "append", "src/Foo.cs:2-4", CancellationToken.None),
            PipeResponse.Success("Queued 1 sentences."));

        Assert.Equal(("speak", "Hello.", "append", "src/Foo.cs:2-4"), (seen.Op, seen.Text, seen.Mode, seen.Focus));
    }

    [Fact]
    public async Task ReadFile_sends_the_path()
    {
        var (text, seen) = await Call(
            tools => tools.ReadFile(@"C:\docs\notes.md", CancellationToken.None),
            PipeResponse.Success("Reading notes.md."));

        Assert.Equal("Reading notes.md.", text);
        Assert.Equal(("read_file", @"C:\docs\notes.md"), (seen.Op, seen.Path));
    }

    [Fact]
    public async Task Stop_sends_the_stop_operation()
    {
        var (text, seen) = await Call(tools => tools.Stop(CancellationToken.None), PipeResponse.Success("Stopped."));

        Assert.Equal("Stopped.", text);
        Assert.Equal("stop", seen.Op);
    }

    public static TheoryData<string> ToolNames => new() { "read_file", "speak", "show_diff", "stop", "status" };

    private static Task<string> CallTool(ReaderTools tools, string tool) => tool switch
    {
        "read_file" => tools.ReadFile("a.md", CancellationToken.None),
        "speak" => tools.Speak("Hello.", "append", null, CancellationToken.None),
        "show_diff" => tools.ShowDiff(SampleDiff.Foo, null, CancellationToken.None),
        "stop" => tools.Stop(CancellationToken.None),
        "status" => tools.Status(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Every_tool_sends_the_chats_process_and_folder(string tool)
    {
        var (_, seen) = await Call(tools => CallTool(tools, tool), PipeResponse.Success("ok"));

        // ChatTabs routes each call to its tab by this process, so a call without it lands in the wrong chat.
        Assert.Equal(tool, seen.Op);
        Assert.Equal(ChatKey, seen.Process);
        Assert.Equal(Environment.CurrentDirectory, seen.Folder);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task An_error_from_the_app_becomes_a_tool_error(string tool)
    {
        var error = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => Call(tools => CallTool(tools, tool), PipeResponse.Fail("No such file.")));

        Assert.Equal("No such file.", error.Message);
    }

    [Theory]
    [InlineData(0, "state: playing\nsource: stream\nsentence: 1 of 2")]
    [InlineData(1, "state: playing\nsource: stream\nsentence: 1 of 2\ndiff: 1 file")]
    [InlineData(3, "state: playing\nsource: stream\nsentence: 1 of 2\ndiff: 3 files")]
    public async Task Status_mentions_a_loaded_diff(int diffFiles, string expected)
    {
        var (text, _) = await Call(tools => tools.Status(CancellationToken.None), PipeResponse.Success(new PipeResult
        {
            Message = "playing, sentence 1 of 2",
            State = "playing",
            Source = "stream",
            CurrentSentence = 1,
            TotalSentences = 2,
            DiffFiles = diffFiles,
        }));

        Assert.Equal(expected, text);
    }
}