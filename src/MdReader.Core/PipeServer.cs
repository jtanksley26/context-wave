using System.IO.Pipes;
using System.Text.Json;

namespace MdReader.Core;

public sealed class PipeServer(string pipeName, Func<PipeRequest, Task<PipeResponse>> handler) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        // Create the first instance synchronously so the pipe exists when Start returns.
        var first = Create();
        _ = Task.Run(() => AcceptLoopAsync(first, _cts.Token));
    }

    public void Dispose() => _cts.Cancel();

    private NamedPipeServerStream Create() => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
