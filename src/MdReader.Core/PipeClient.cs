using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace MdReader.Core;

public static class PipeClient
{
    /// <exception cref="TimeoutException">No server accepted the connection in time.</exception>
    public static async Task<PipeResponse> SendAsync(
        string pipeName, PipeRequest request, int connectTimeoutMs, CancellationToken ct = default)
    {
        // Not PipeOptions.CurrentUserOnly: it refuses a pipe whose owner differs from this process's
        // default owner, which is the case for a hook run by Claude Code. The owner is checked below.
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs, ct);

        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!PipeOwner.IsTrustedForCurrentUser(owner))
            throw new UnauthorizedAccessException("The pipe is not owned by the current user.");

        var bytes = PipeProtocol.Utf8.GetBytes(JsonSerializer.Serialize(request, PipeProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);

        using var reader = new StreamReader(pipe, PipeProtocol.Utf8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct)
                   ?? throw new IOException("Context Wave closed the connection without replying.");
        return JsonSerializer.Deserialize<PipeResponse>(line, PipeProtocol.Json)
               ?? throw new IOException("Context Wave sent an empty reply.");
    }
}
