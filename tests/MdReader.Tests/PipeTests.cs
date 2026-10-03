using System.IO.Pipes;
using MdReader.Core;

namespace MdReader.Tests;

public class PipeTests
{
    private static string NewName() => $"MdReader.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task Request_and_response_round_trip()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success(new PipeResult { Message = "ok", TotalSentences = 3 }));
        });
        server.Start();

        var response = await PipeClient.SendAsync(
            name, new PipeRequest { Op = "speak", Text = "Line one.\nLine \"two\" é.", Mode = "append" }, 2000);

        Assert.True(response.Ok);
        Assert.Equal("ok", response.Result!.Message);
        Assert.Equal(3, response.Result.TotalSentences);
        Assert.Equal("Line one.\nLine \"two\" é.", seen!.Text);
    }

    [Fact]
    public async Task Diff_fields_round_trip()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success(new PipeResult { Message = "ok", DiffFiles = 2 }));
        });
        server.Start();

        var response = await PipeClient.SendAsync(
            name,
            new PipeRequest { Op = "show_diff", Diff = SampleDiff.Many, Title = "PR 12", Focus = "src/Foo.cs:2-4" },
            2000);

        Assert.Equal(2, response.Result!.DiffFiles);
        Assert.Equal((SampleDiff.Many, "PR 12", "src/Foo.cs:2-4"), (seen!.Diff, seen.Title, seen.Focus));
    }

    [Fact]
    public async Task Server_handles_several_requests_in_a_row()
    {
        var name = NewName();
        using var server = new PipeServer(name, r => Task.FromResult(PipeResponse.Success(r.Op)));
        server.Start();

        foreach (var op in new[] { "a", "b", "c" })
            Assert.Equal(op, (await PipeClient.SendAsync(name, new PipeRequest { Op = op }, 2000)).Result!.Message);
    }

    [Fact]
    public async Task Handler_exception_becomes_an_error_response()
    {
        var name = NewName();
        using var server = new PipeServer(name, _ => throw new InvalidOperationException("boom"));
        server.Start();

        var response = await PipeClient.SendAsync(name, new PipeRequest { Op = "x" }, 2000);
        Assert.False(response.Ok);
        Assert.Equal("boom", response.Error);
    }

    [Fact]
    public async Task Malformed_request_becomes_an_error_response()
    {
        var name = NewName();
        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Success("unused")));
        server.Start();

        await using var pipe = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000);
        await pipe.WriteAsync(PipeProtocol.Utf8.GetBytes("not json\n"));
        using var reader = new StreamReader(pipe, PipeProtocol.Utf8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await reader.ReadLineAsync(timeout.Token);

        Assert.Contains("\"ok\":false", line);
    }

    [Fact]
    public async Task Server_survives_a_client_that_connects_and_closes_without_sending()
    {
        var name = NewName();
        using var server = new PipeServer(name, r => Task.FromResult(PipeResponse.Success(r.Op)));
        server.Start();

        var silent = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await silent.ConnectAsync(2000);
        await silent.DisposeAsync();

        var response = await PipeClient.SendAsync(name, new PipeRequest { Op = "ping" }, 2000);
        Assert.True(response.Ok);
        Assert.Equal("ping", response.Result!.Message);
    }

    [Fact]
    public async Task Client_times_out_when_no_server_is_listening() =>
        await Assert.ThrowsAsync<TimeoutException>(
            () => PipeClient.SendAsync(NewName(), new PipeRequest { Op = "status" }, 200));
}
