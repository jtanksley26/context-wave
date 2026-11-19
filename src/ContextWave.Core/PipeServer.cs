using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace ContextWave.Core;

/// <param name="isTrustedOwner">Decides whether the pipe's owner is this user; tests replace it.</param>
public sealed class PipeServer(
    string pipeName,
    Func<PipeRequest, Task<PipeResponse>> handler,
    Func<SecurityIdentifier?, bool>? isTrustedOwner = null) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        // Create the first instance synchronously so the pipe exists when Start returns.
        var first = Create();
        _ = Task.Run(() => AcceptLoopAsync(first, _cts.Token));
    }

    public void Dispose() => _cts.Cancel();

    /// <exception cref="UnauthorizedAccessException">Another user created the pipe first.</exception>
    private NamedPipeServerStream Create()
    {
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        // The name is predictable, so another user can create the pipe first and let anyone in. This
        // instance then joins their pipe, which they own, and they could connect and send requests.
        SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            owner = null;
        }
        if ((isTrustedOwner ?? PipeOwner.IsTrustedForCurrentUser)(owner)) return pipe;

        pipe.Dispose();
        throw new UnauthorizedAccessException("Context Wave's pipe was created by another user.");
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream? pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pipe ??= Create();
                await pipe.WaitForConnectionAsync(ct);

                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"Pipe server error: {ex.Message}");
                pipe?.Dispose();
                pipe = null;
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        pipe?.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, PipeProtocol.Utf8, false, 4096, leaveOpen: true);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(readTimeout.Token);

                PipeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<PipeRequest>(line ?? "", PipeProtocol.Json)
                                  ?? throw new JsonException();
                    response = await handler(request);
                }
                catch (JsonException)
                {
                    response = PipeResponse.Fail("Malformed request.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = PipeResponse.Fail(ex.Message);
                }

                var bytes = PipeProtocol.Utf8.GetBytes(JsonSerializer.Serialize(response, PipeProtocol.Json) + "\n");
                await pipe.WriteAsync(bytes, ct);
                await pipe.FlushAsync(ct);
                pipe.WaitForPipeDrain();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The client went away or the server is shutting down.
            }
        }
    }
}
