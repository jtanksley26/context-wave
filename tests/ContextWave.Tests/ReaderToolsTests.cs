using ContextWave.Bridge;
using ContextWave.Core;

namespace ContextWave.Tests;

public class ReaderToolsTests
{
    private static string NewName() => $"ContextWave.Test.{Guid.NewGuid():N}";

    private static ReaderTools Tools(string name) => new(new AppLink(name, () => false, TimeSpan.FromSeconds(2)));

    [Fact]
    public async Task ShowDiff_sends_the_diff_and_title()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Showing diff: 1 file."));
        });
        server.Start();

        var text = await Tools(name).ShowDiff(SampleDiff.Foo, "PR 12", CancellationToken.None);

        Assert.Equal("Showing diff: 1 file.", text);
        Assert.Equal(("show_diff", SampleDiff.Foo, "PR 12"), (seen!.Op, seen.Diff, seen.Title));
    }

    [Fact]
    public async Task Speak_sends_the_focus()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Queued 1 sentences."));
        });
        server.Start();

        await Tools(name).Speak("Hello.", "append", "src/Foo.cs:2-4", CancellationToken.None);

        Assert.Equal(("speak", "Hello.", "append", "src/Foo.cs:2-4"), (seen!.Op, seen.Text, seen.Mode, seen.Focus));
    }

    [Fact]
    public async Task ReadFile_sends_the_path()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Reading notes.md."));
        });
        server.Start();

        var text = await Tools(name).ReadFile(@"C:\docs\notes.md", CancellationToken.None);

        Assert.Equal("Reading notes.md.", text);
        Assert.Equal(("read_file", @"C:\docs\notes.md"), (seen!.Op, seen.Path));
    }

    [Fact]
    public async Task Stop_sends_the_stop_operation()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Stopped."));
        });
        server.Start();

        var text = await Tools(name).Stop(CancellationToken.None);

        Assert.Equal("Stopped.", text);
        Assert.Equal("stop", seen!.Op);
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
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("ok"));
        });
        server.Start();

        await CallTool(Tools(name), tool);

        // ChatTabs routes each call to its tab by this process, so a call without it lands in the wrong chat.
        Assert.Equal(tool, seen!.Op);
        Assert.NotNull(ProcessIdentity.ParentKey());
        Assert.Equal(ProcessIdentity.ParentKey(), seen.Process);
        Assert.Equal(Environment.CurrentDirectory, seen.Folder);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task An_error_from_the_app_becomes_a_tool_error(string tool)
    {
        var name = NewName();
        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Fail("No such file.")));
        server.Start();

        var error = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => CallTool(Tools(name), tool));

        Assert.Equal("No such file.", error.Message);
    }

    [Theory]
    [InlineData(0, "state: playing\nsource: stream\nsentence: 1 of 2")]
    [InlineData(1, "state: playing\nsource: stream\nsentence: 1 of 2\ndiff: 1 file")]
    [InlineData(3, "state: playing\nsource: stream\nsentence: 1 of 2\ndiff: 3 files")]
    public async Task Status_mentions_a_loaded_diff(int diffFiles, string expected)
    {
        var name = NewName();
        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Success(new PipeResult
        {
            Message = "playing, sentence 1 of 2",
            State = "playing",
            Source = "stream",
            CurrentSentence = 1,
            TotalSentences = 2,
            DiffFiles = diffFiles,
        })));
        server.Start();

        Assert.Equal(expected, await Tools(name).Status(CancellationToken.None));
    }
}
