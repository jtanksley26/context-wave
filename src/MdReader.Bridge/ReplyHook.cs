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
    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(2);

    // The end of a transcript is enough to find the last reply; whole files run to many megabytes.
    private const int TranscriptTailBytes = 2 * 1024 * 1024;

    /// <summary>What a Stop event carried. Fields is the list of property names, for the log.</summary>
    private sealed record StopEvent(string? Message, string? TranscriptPath, string Fields);

    public static Task<int> RunAsync(TextReader input, Func<PipeRequest, Task<PipeResponse>> send) =>
        RunAsync(input, send, transcriptAttempts: 6, transcriptDelayMs: 300);

    public static async Task<int> RunAsync(
        TextReader input, Func<PipeRequest, Task<PipeResponse>> send, int transcriptAttempts, int transcriptDelayMs)
    {
        try
        {
            // Claude Code closes standard input after the message; do not wait forever if something else does not.
            var reading = input.ReadToEndAsync();
            if (await Task.WhenAny(reading, Task.Delay(InputTimeout)) != reading) return 0;
            if (ParseStop(await reading) is not { } stop) return 0;

            // Newer versions of Claude Code hand over the reply; older ones only say where the transcript is.
            var reply = stop.Message
                        ?? await ReplyFromTranscriptAsync(stop.TranscriptPath, transcriptAttempts, transcriptDelayMs);
            if (reply is null)
            {
                FileLog.Write($"reply-hook: no reply text in the Stop event or its transcript (fields: {stop.Fields}).");
                return 0;
            }
            await send(new PipeRequest { Op = "speak_reply", Text = reply });
        }
        catch (Exception)
        {
            // Not running, pipe trouble, anything: reading a reply aloud is never worth failing a hook.
        }
        return 0;
    }

    /// <summary>The reply text a Stop event carries itself, or null when it carries none.</summary>
    public static string? ExtractReply(string hookInput) => ParseStop(hookInput)?.Message;

    /// <summary>The transcript file a Stop event names, or null.</summary>
    public static string? ExtractTranscriptPath(string hookInput) => ParseStop(hookInput)?.TranscriptPath;

    private static StopEvent? ParseStop(string hookInput)
    {
        if (string.IsNullOrWhiteSpace(hookInput)) return null;
        try
        {
            using var document = JsonDocument.Parse(hookInput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Text(root, "hook_event_name") != "Stop") return null;

            return new StopEvent(
                Text(root, "last_assistant_message"),
                Text(root, "transcript_path"),
                string.Join(", ", root.EnumerateObject().Select(p => p.Name)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string property's value, or null when it is missing, not a string, or blank.</summary>
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads the last reply from a session transcript. The transcript is written a moment after the
    /// reply is shown, so this looks again a few times before giving up.
    /// </summary>
    public static async Task<string?> ReplyFromTranscriptAsync(string? path, int attempts, int delayMs)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(delayMs);
            if (LastReply(ReadTail(path)) is { } reply) return reply;
        }
        return null;
    }

    /// <summary>
    /// The text of the reply that ends a transcript: the text of the assistant entries after the last
    /// user entry (a prompt or a tool result). Each line is one JSON entry; other kinds of entry,
    /// subagent entries and lines that do not parse are skipped.
    /// </summary>
    public static string? LastReply(IReadOnlyList<string> lines)
    {
        var parts = new List<string>();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                using var document = JsonDocument.Parse(lines[i]);
                var entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;

                var type = Text(entry, "type");
                if (type == "user") break;
                if (type != "assistant") continue;
                if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                var texts = content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object && Text(block, "type") == "text")
                    .Select(block => Text(block, "text"))
                    .Where(text => text is not null);
                // Walking backwards, so this entry's text goes in front of what was found after it.
                parts.InsertRange(0, texts!);
            }
            catch (JsonException)
            {
                // A line still being written, or the cut-off first line of the tail.
            }
        }

        var reply = string.Join("\n\n", parts).Trim();
        return reply.Length == 0 ? null : reply;
    }

    /// <summary>The lines at the end of a file that another process may still be writing.</summary>
    private static string[] ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipped = stream.Length > TranscriptTailBytes;
            if (skipped) stream.Seek(-TranscriptTailBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, PipeProtocol.Utf8);
            var lines = reader.ReadToEnd().Split('\n');
            // After a seek the first line is the tail end of a longer one.
            return skipped ? lines[1..] : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Sends to an app that is already running; never starts one.</summary>
    public static Func<PipeRequest, Task<PipeResponse>> SendTo(string pipeName) => async request =>
    {
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        return await PipeClient.SendAsync(pipeName, request, ConnectTimeoutMs, timeout.Token);
    };
}
