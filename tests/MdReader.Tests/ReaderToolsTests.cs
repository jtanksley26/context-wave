using MdReader.Bridge;
using MdReader.Core;

namespace MdReader.Tests;

public class ReaderToolsTests
{
    private static string NewName() => $"MdReader.Test.{Guid.NewGuid():N}";

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
