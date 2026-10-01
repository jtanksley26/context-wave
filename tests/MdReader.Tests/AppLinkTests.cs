using MdReader.Bridge;
using MdReader.Core;

namespace MdReader.Tests;

public class AppLinkTests
{
    private static string NewName() => $"MdReader.Test.{Guid.NewGuid():N}";

    private static PipeServer Echo(string name) =>
        new(name, request => Task.FromResult(PipeResponse.Success(request.Op)));

    [Fact]
    public async Task Sends_directly_when_the_app_is_running()
    {
        var name = NewName();
        using var server = Echo(name);
        server.Start();
        var launched = false;
        var link = new AppLink(name, () => launched = true, TimeSpan.FromSeconds(2));

        var response = await link.SendAsync(new PipeRequest { Op = "status" });

        Assert.Equal("status", response.Result!.Message);
        Assert.False(launched);
    }

    [Fact]
    public async Task Launches_the_app_and_retries_when_it_is_not_running()
    {
        var name = NewName();
        PipeServer? server = null;
        var link = new AppLink(name, () =>
        {
            server = Echo(name);
            server.Start();
            return true;
        }, TimeSpan.FromSeconds(5));

        var response = await link.SendAsync(new PipeRequest { Op = "stop" });

        Assert.Equal("stop", response.Result!.Message);
        server!.Dispose();
    }

    [Fact]
    public async Task Fails_when_the_app_cannot_be_launched()
    {
        var link = new AppLink(NewName(), () => false, TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => link.SendAsync(new PipeRequest { Op = "status" }));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Fails_when_the_app_never_starts_listening()
    {
        var link = new AppLink(NewName(), () => true, TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => link.SendAsync(new PipeRequest { Op = "status" }));
        Assert.Contains("did not start", ex.Message);
    }
}
