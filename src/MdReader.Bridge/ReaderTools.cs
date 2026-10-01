using System.ComponentModel;
using MdReader.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MdReader.Bridge;

[McpServerToolType]
public sealed class ReaderTools(AppLink link)
{
    [McpServerTool(Name = "read_file")]
    [Description("Read a markdown or text file aloud in the MD Reader window. Replaces whatever is being read.")]
    public Task<string> ReadFile(
        [Description("Absolute path to a .md, .markdown or .txt file.")] string path,
        CancellationToken ct) =>
        Call(new PipeRequest { Op = "read_file", Path = path }, ct);

    [McpServerTool(Name = "speak")]
    [Description(
        "Read markdown text aloud in the MD Reader window. Call it repeatedly to stream: with mode 'append' " +
        "each call adds to the end of the current document and reading continues without a gap. " +
        "Send whole paragraphs, not fragments of a sentence.")]
    public Task<string> Speak(
        [Description("Markdown text to read.")] string text,
        [Description("'append' (default) adds to the current document; 'replace' starts a new one.")]
        string mode = "append",
        CancellationToken ct = default) =>
        Call(new PipeRequest { Op = "speak", Text = text, Mode = mode }, ct);

    [McpServerTool(Name = "stop")]
    [Description("Stop reading and clear the MD Reader document and queue.")]
    public Task<string> Stop(CancellationToken ct) => Call(new PipeRequest { Op = "stop" }, ct);

    [McpServerTool(Name = "status")]
    [Description("Report whether MD Reader is playing, paused or idle, what it is reading, and its position.")]
    public Task<string> Status(CancellationToken ct) => Call(new PipeRequest { Op = "status" }, ct);

    private async Task<string> Call(PipeRequest request, CancellationToken ct)
    {
        PipeResponse response;
        try
        {
            response = await link.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            throw new McpException(ex.Message);
        }

        if (!response.Ok) throw new McpException(response.Error ?? "MD Reader reported an error.");
        var result = response.Result ?? new PipeResult();
        return result.State is null
            ? result.Message
            : $"state: {result.State}\nsource: {result.Source}\nsentence: {result.CurrentSentence} of {result.TotalSentences}";
    }
}
