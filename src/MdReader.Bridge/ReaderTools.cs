using System.ComponentModel;
using System.Text.Json;
using MdReader.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MdReader.Bridge;

[McpServerToolType]
public sealed class ReaderTools(AppLink link)
{
    [McpServerTool(Name = "read_file")]
    [Description(
        "Read a markdown or text file aloud in the Context Wave window. Replaces whatever is being read. " +
        "Returns as soon as reading has started; it does not wait for the reading to finish. " +
        "Use status to check progress.")]
    public Task<string> ReadFile(
        [Description("Absolute path to a .md, .markdown or .txt file.")] string path,
        CancellationToken ct) =>
        Call(new PipeRequest { Op = "read_file", Path = path }, ct);

    [McpServerTool(Name = "speak")]
    [Description(
        "Read markdown text aloud in the Context Wave window. Call it repeatedly to stream: with mode 'append' " +
        "each call adds to the end of the current document and reading continues without a gap. " +
        "Send whole paragraphs, not fragments of a sentence. " +
        "During a code walkthrough (after show_diff), pass focus so the diff follows what is being said. " +
        "Returns as soon as the text is queued; it does not wait for it to be spoken.")]
    public Task<string> Speak(
        [Description("Markdown text to read.")] string text,
        [Description(
            "'append' (default) adds to the current document; 'replace' interrupts the current reading " +
            "and starts a new document.")]
        string mode = "append",
        [Description(
            "Optional. The part of the shown diff this text is about: 'path', 'path:line' or " +
            "'path:start-end', using the path as it appears in the diff and line numbers from the new " +
            "version of the file. One call, one focus: start a new call when you move to another place.")]
        string? focus = null,
        CancellationToken ct = default) =>
        Call(new PipeRequest { Op = "speak", Text = text, Mode = mode, Focus = focus }, ct);

    [McpServerTool(Name = "show_diff")]
    [Description(
        "Show a unified diff in the Context Wave window beside the text being read. Use it when reviewing a " +
        "pull request or walking the user through code changes: call show_diff once with the whole diff " +
        "(for example the output of 'git diff' or 'gh pr diff'), then call speak once per point, each " +
        "with a focus naming the file and lines that point is about. Replaces the current document and " +
        "any earlier diff. Returns as soon as the diff is displayed.")]
    public Task<string> ShowDiff(
        [Description("Unified diff text, at most 2 MB.")] string diff,
        [Description("Optional short title for the window, such as the pull request name.")]
        string? title = null,
        CancellationToken ct = default) =>
        Call(new PipeRequest { Op = "show_diff", Diff = diff, Title = title }, ct);

    [McpServerTool(Name = "stop")]
    [Description("Stop reading and clear the Context Wave document and queue.")]
    public Task<string> Stop(CancellationToken ct) => Call(new PipeRequest { Op = "stop" }, ct);

    [McpServerTool(Name = "status")]
    [Description(
        "Report whether Context Wave is playing, paused or idle, what it is reading, its position, and " +
        "whether a diff is shown.")]
    public Task<string> Status(CancellationToken ct) => Call(new PipeRequest { Op = "status" }, ct);

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private async Task<string> Call(PipeRequest request, CancellationToken ct)
    {
        PipeResponse response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            response = await link.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new McpException("Context Wave is not responding.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or TimeoutException or Win32Exception or JsonException)
        {
            throw new McpException(ex.Message);
        }

        if (!response.Ok) throw new McpException(response.Error ?? "Context Wave reported an error.");
        var result = response.Result ?? new PipeResult();
        if (result.State is null) return result.Message;
        var status =
            $"state: {result.State}\nsource: {result.Source}\nsentence: {result.CurrentSentence} of {result.TotalSentences}";
        return result.DiffFiles is int files and > 0
            ? $"{status}\ndiff: {files} {(files == 1 ? "file" : "files")}"
            : status;
    }
}
