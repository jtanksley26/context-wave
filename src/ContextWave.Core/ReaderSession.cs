using System.Net;

namespace ContextWave.Core;

public sealed class ReaderException(string message) : Exception(message);

/// <param name="FocusProblem">Why the focus could not be linked to the diff, or null.</param>
public sealed record SpeakResult(int Count, string? FocusProblem);

/// <summary>What to do with one of Claude's replies once it is added to the document.</summary>
public enum ReplyAction
{
    /// <summary>Add it without reading it.</summary>
    Hold,

    /// <summary>Read it after whatever is being read, or now when nothing is.</summary>
    Wait,

    /// <summary>Read it now, stopping whatever is being read.</summary>
    Interrupt,
}

public sealed class ReaderSession(ReadingQueue queue, Func<bool> announceCodeBlocks, Func<bool> voiceReady)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxDiffBytes = 2 * 1024 * 1024;
    public const int MaxReplyChars = 200_000;

    /// <summary>The <see cref="Source"/> while one of Claude's replies is the current document.</summary>
    public const string ReplySource = "reply";

    /// <summary>The <see cref="Source"/> while text Claude sent with speak is the current document.</summary>
    public const string StreamSource = "stream";

    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    private MarkdownDocument _document = new();

    private DiffDocument? _diff;

    // Sentence id -> the place in the diff its chunk was spoken about.
    private readonly Dictionary<int, DiffAnchor> _anchors = [];

    /// <summary>
    /// "" when empty, <see cref="StreamSource"/> for text Claude sent with speak, <see cref="ReplySource"/> for a reply read
    /// automatically, otherwise the file path.
    /// </summary>
    public string Source { get; private set; } = "";

    /// <summary>The title given with the loaded diff, or null.</summary>
    public string? DiffTitle { get; private set; }

    public event Action<string>? DocumentReplaced;
    public event Action<string>? DocumentAppended;

    /// <summary>The diff HTML and title; "" and null when the diff is unloaded.</summary>
    public event Action<string, string?>? DiffReplaced;

    /// <param name="hold">
    /// Load what a read_file or speak asks for without starting to read it: the tab is in the
    /// background, waiting its turn.
    /// </param>
    public PipeResponse Handle(PipeRequest request, bool hold = false)
    {
        try
        {
            switch (request.Op)
            {
                case PipeOps.ReadFile:
                    RequireDrivePath(request.Path ?? "");
                    RequireVoice();
                    OpenFile(request.Path ?? "", hold);
                    return PipeResponse.Success(
                        $"{(hold ? "Waiting to read" : "Reading")} {Path.GetFileName(Source)} ({queue.Count} sentences).");
                case PipeOps.Speak:
                    RequireVoice();
                    var spoken = Speak(request.Text ?? "", request.Mode ?? "append", request.Focus, hold);
                    var queued = $"Queued {spoken.Count} sentences{(hold ? " to read when this chat's turn comes" : "")}.";
                    return PipeResponse.Success(
                        spoken.FocusProblem is null ? queued : $"{queued} {spoken.FocusProblem}");
                case PipeOps.ShowDiff:
                    var files = ShowDiff(request.Diff ?? "", request.Title);
                    return PipeResponse.Success($"Showing diff: {files} {(files == 1 ? "file" : "files")}.");
                case PipeOps.Stop:
                    Stop();
                    return PipeResponse.Success("Stopped.");
                case PipeOps.Status:
                    return PipeResponse.Success(Status());
                default:
                    return PipeResponse.Fail($"Unknown operation '{request.Op}'.");
            }
        }
        catch (ReaderException ex)
        {
            return PipeResponse.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return PipeResponse.Fail($"Context Wave could not complete the request: {ex.Message}");
        }
    }

    /// <summary>
    /// Refuses a path sent by a chat that is not on a drive. Looking at a UNC or device path makes
    /// Windows connect to that host as the user, so a chat tricked into reading one would hand over
    /// the user's credentials. Files the user opens themselves can still be on a network share.
    /// </summary>
    private static void RequireDrivePath(string path)
    {
        if (path.StartsWith('\\') || path.StartsWith('/'))
            throw new ReaderException($"Only files on a drive can be read: {path}");
    }

    public void OpenFile(string path, bool hold = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ReaderException($"Path must be absolute: {path}");
        var extension = Path.GetExtension(path);
        if (!Extensions.Contains(extension))
            throw new ReaderException($"Unsupported file type '{extension}'. Use .md, .markdown or .txt.");

        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new ReaderException($"File not found: {path}");
            if (info.Length > MaxFileBytes) throw new ReaderException("File is too large (limit 5 MB).");
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ReaderException($"File not found: {path}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or System.Security.SecurityException or IOException
                                       or UnauthorizedAccessException)
        {
            throw new ReaderException($"Could not read the file: {ex.Message}");
        }

        ClearDiff();
        Replace(text, path, hold);
    }

    public SpeakResult Speak(string text, string mode, string? focus = null, bool hold = false)
    {
        if (mode is not ("append" or "replace"))
            throw new ReaderException($"Unknown mode '{mode}'. Use 'append' or 'replace'.");
        if (string.IsNullOrWhiteSpace(text)) throw new ReaderException("The text is empty.");

        DiffAnchor? anchor = null;
        string? problem = null;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            if (_diff is null) problem = $"Focus '{focus}' was ignored because no diff is loaded.";
            else anchor = _diff.Resolve(focus, out problem);
        }

        var replace = mode == "replace" || Source == "";
        if (replace)
        {
            _document = new MarkdownDocument();
            _anchors.Clear();
        }

        var result = _document.Append(text, announceCodeBlocks());
        var html = result.Html;
        if (anchor is not null && result.Sentences.Count > 0)
        {
            foreach (var sentence in result.Sentences) _anchors[sentence.Id] = anchor;
            html = $"<div class=\"focus-label\">{WebUtility.HtmlEncode(anchor.Label)}</div>{html}";
        }

        if (replace)
        {
            Source = StreamSource;
            DocumentReplaced?.Invoke(html);
            queue.Load(result.Sentences, voiceReady() && !hold);
        }
        else
        {
            if (Source == ReplySource) Source = StreamSource;
            DocumentAppended?.Invoke(html);
            queue.Append(result.Sentences, voiceReady() && !hold);
        }
        return new SpeakResult(result.Sentences.Count, problem);
    }

    /// <summary>
    /// Appends one of Claude's replies and holds, queues or reads it as <paramref name="action"/> says.
    /// Skipped while a walkthrough is still to be read; replaces the walkthrough once it has been.
    /// </summary>
    /// <returns>What happened, for the caller's log.</returns>
    public string SpeakReply(string text, ReplyAction action)
    {
        if (ReplySkipReason(text, announceCodeBlocks()) is { } skipped) return skipped;

        var busy = queue.State != ReadingState.Idle;
        if (_diff is not null)
        {
            // A document that was loaded but not played (opened without the voice) is unread, not free.
            // After a document finishes, CurrentIndex is the sentence count. A diff nobody has
            // narrated yet has no sentences at all, and is still to be read too.
            if (busy || queue.Count == 0 || queue.CurrentIndex < queue.Count) return "Skipped: a walkthrough is showing.";
            queue.Stop();
            ClearDiff();
            _document = new MarkdownDocument();
            Source = "";
        }
        var voiceMissing = action != ReplyAction.Hold && !voiceReady();

        var first = _document.IsEmpty;
        var added = _document.Append(text, announceCodeBlocks());
        var count = added.Sentences.Count;
        // Set before the document is announced, so listeners see what it now holds.
        if (Source == "") Source = ReplySource;
        if (first) DocumentReplaced?.Invoke(added.Html);
        else DocumentAppended?.Invoke($"<hr>{added.Html}");

        if (action == ReplyAction.Hold || voiceMissing)
        {
            queue.Append(added.Sentences, autoPlay: false, holdBack: true);
            return $"Added the reply ({count} sentences) without reading it{(voiceMissing ? ": the voice is not ready" : "")}.";
        }
        if (busy && action == ReplyAction.Wait)
        {
            // Reading carries on into it; paused, it stays paused.
            queue.Append(added.Sentences);
            return $"Queued the reply ({count} sentences) after the current one.";
        }
        queue.Append(added.Sentences, autoPlay: false);
        queue.JumpTo(added.Sentences[0].Id);
        Source = ReplySource;
        return $"Reading the reply ({count} sentences).";
    }

    /// <summary>Why a reply would be skipped whatever the reader is doing, or null when it can be read.</summary>
    public static string? ReplySkipReason(string text, bool announceCodeBlocks)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Skipped: the reply is empty.";
        if (text.Length > MaxReplyChars) return "Skipped: the reply is too long.";
        // Parsed on a scratch document so a reply with nothing to read leaves the reader alone.
        if (new MarkdownDocument().Append(text, announceCodeBlocks).Sentences.Count == 0)
            return "Skipped: there is nothing to read in the reply.";
        return null;
    }

    /// <summary>Starts a walkthrough: stops reading, clears the document and shows the diff.</summary>
    /// <returns>The number of files in the diff.</returns>
    public int ShowDiff(string diff, string? title)
    {
        if (string.IsNullOrWhiteSpace(diff)) throw new ReaderException("The diff is empty.");
        if (PipeProtocol.Utf8.GetByteCount(diff) > MaxDiffBytes)
            throw new ReaderException("Diff is too large (limit 2 MB).");
        // Parse before touching anything so a bad diff leaves the current reading alone.
        var parsed = DiffDocument.Parse(diff);

        queue.Stop();
        _document = new MarkdownDocument();
        _anchors.Clear();
        _diff = parsed;
        DiffTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Source = StreamSource;
        DocumentReplaced?.Invoke("");
        DiffReplaced?.Invoke(parsed.Html, DiffTitle);
        return parsed.Files.Count;
    }

    /// <summary>The place in the diff that sentence was spoken about, or null.</summary>
    public DiffAnchor? AnchorFor(int sentenceId) => _anchors.GetValueOrDefault(sentenceId);

    /// <summary>
    /// The first sentence to jump to for a click in the diff. With a line: a sentence whose range covers
    /// it, otherwise one linked to the whole file. Without a line (the file header): any sentence linked
    /// to that file.
    /// </summary>
    public int? SentenceForDiff(int fileIndex, int? lineIndex)
    {
        int? inRange = null, wholeFile = null, any = null;
        foreach (var (id, anchor) in _anchors)
        {
            if (anchor.FileIndex != fileIndex) continue;
            any = Lower(any, id);
            if (anchor.FirstLine is null) wholeFile = Lower(wholeFile, id);
            else if (lineIndex >= anchor.FirstLine && lineIndex <= anchor.LastLine) inRange = Lower(inRange, id);
        }
        return lineIndex is null ? any : inRange ?? wholeFile;

        static int Lower(int? current, int id) => current is { } value && value < id ? value : id;
    }

    public void Stop()
    {
        queue.Stop();
        _document = new MarkdownDocument();
        Source = "";
        ClearDiff();
        DocumentReplaced?.Invoke("");
    }

    public PipeResult Status()
    {
        var total = queue.Count;
        var current = queue.Position;
        var state = queue.StateName;
        return new PipeResult
        {
            Message = $"{state}, sentence {current} of {total}",
            State = state,
            Source = Source,
            CurrentSentence = current,
            TotalSentences = total,
            DiffFiles = _diff?.Files.Count ?? 0,
        };
    }

    private void Replace(string markdown, string source, bool hold)
    {
        _document = new MarkdownDocument(Path.GetDirectoryName(source));
        var result = _document.Append(markdown, announceCodeBlocks());
        Source = source;
        DocumentReplaced?.Invoke(result.Html);
        queue.Load(result.Sentences, voiceReady() && !hold);
    }

    private void ClearDiff()
    {
        _anchors.Clear();
        if (_diff is null) return;
        _diff = null;
        DiffTitle = null;
        DiffReplaced?.Invoke("", null);
    }

    private void RequireVoice()
    {
        if (!voiceReady())
            throw new ReaderException("Voice not ready. Open Context Wave and download the voice first.");
    }
}
