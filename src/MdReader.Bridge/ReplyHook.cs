using System.Text.Json;
using MdReader.Core;

namespace MdReader.Bridge;

/// <summary>
/// The command Claude Code runs when a reply ends ("MdReader.Bridge.exe reply-hook"). It forwards
/// the reply to a running MD Reader. A hook must never disturb Claude, so this prints nothing,
/// swallows every failure and always returns 0.
/// </summary>
public static class ReplyHook
{
    private const int ConnectTimeoutMs = 300;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(TextReader input, Func<PipeRequest, Task<PipeResponse>> send)
    {
        try
        {
            var reply = ExtractReply(await input.ReadToEndAsync());
            if (reply is not null) await send(new PipeRequest { Op = "speak_reply", Text = reply });
        }
        catch (Exception)
        {
            // Not running, pipe trouble, anything: reading a reply aloud is never worth failing a hook.
        }
        return 0;
    }

    /// <summary>The reply text from a Stop event's JSON, or null when there is nothing to read.</summary>
    public static string? ExtractReply(string hookInput)
    {
        if (string.IsNullOrWhiteSpace(hookInput)) return null;
        try
        {
            using var document = JsonDocument.Parse(hookInput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("hook_event_name", out var name)
                || name.ValueKind != JsonValueKind.String || name.GetString() != "Stop")
                return null;

            if (!root.TryGetProperty("last_assistant_message", out var message)
                || message.ValueKind != JsonValueKind.String)
            {
                // The one assumption this feature rests on; say so if Claude Code sends something else.
                var fields = string.Join(", ", root.EnumerateObject().Select(p => p.Name));
                FileLog.Write($"reply-hook: the Stop event had no last_assistant_message text (fields: {fields}).");
                return null;
            }

            var text = message.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends to an app that is already running; never starts one.</summary>
    public static Func<PipeRequest, Task<PipeResponse>> SendTo(string pipeName) => async request =>
    {
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        return await PipeClient.SendAsync(pipeName, request, ConnectTimeoutMs, timeout.Token);
    };
}
