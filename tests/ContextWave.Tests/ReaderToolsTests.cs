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
            Version = AppVersion.Current,
        })));
        server.Start();

        // The version line follows.
        Assert.Equal($"{expected}\nversion: {AppVersion.Current}", await Tools(name).Status(CancellationToken.None));
    }

    private static async Task<string> StatusWithVersion(string? version)
    {
        var name = NewName();
        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Success(new PipeResult
        {
            Message = "idle", State = "idle", Source = "", CurrentSentence = 0, TotalSentences = 0, Version = version,
        })));
        server.Start();
        return await Tools(name).Status(CancellationToken.None);
    }

    [Fact]
    public async Task Status_shows_the_app_and_bridge_versions()
    {
        var text = await StatusWithVersion(AppVersion.Current);

        Assert.EndsWith($"\nversion: {AppVersion.Current}", text);
        Assert.DoesNotContain("app version", text);
        Assert.DoesNotContain("different builds", text);
    }

    [Fact]
    public void A_bridge_from_another_build_says_so()
    {
        var text = ReaderTools.VersionLines("2026.01.01+aaaaaaa", "2026.02.02+bbbbbbb");

        Assert.Contains("app version: 2026.01.01+aaaaaaa", text);
        Assert.Contains("bridge version: 2026.02.02+bbbbbbb", text);
        Assert.Contains("different builds", text);
    }

    [Theory]
    [InlineData("dev", "2026.02.02+bbbbbbb")]
    [InlineData("2026.01.01+aaaaaaa", "dev")]
    public void A_dev_build_on_either_side_is_not_called_a_different_build(string app, string bridge)
    {
        var text = ReaderTools.VersionLines(app, bridge);

        Assert.Contains($"app version: {app}", text);
        Assert.Contains($"bridge version: {bridge}", text);
        Assert.DoesNotContain("different builds", text);
    }

    [Fact]
    public async Task An_app_that_reports_no_version_is_shown_as_unknown()
    {
        var text = await StatusWithVersion(null);

        Assert.Contains("app version: unknown", text);
        Assert.Contains($"bridge version: {AppVersion.Current}", text);
        Assert.DoesNotContain("different builds", text);
    }
}
