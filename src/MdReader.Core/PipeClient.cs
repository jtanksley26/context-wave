using System.IO.Pipes;
using System.Text.Json;

namespace MdReader.Core;

public static class PipeClient
{
    /// <exception cref="TimeoutException">No server accepted the connection in time.</exception>
    public static async Task<PipeResponse> SendAsync(
        string pipeName, PipeRequest request, int connectTimeoutMs, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeoutMs, ct);

        var bytes = PipeProtocol.Utf8.GetBytes(JsonSerializer.Serialize(request, PipeProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);

        using var reader = new StreamReader(pipe, PipeProtocol.Utf8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct)
                   ?? throw new IOException("MD Reader closed the connection without replying.");
        return JsonSerializer.Deserialize<PipeResponse>(line, PipeProtocol.Json)
               ?? throw new IOException("MD Reader sent an empty reply.");
    }
}
