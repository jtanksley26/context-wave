namespace MdReader.Core;

public sealed class ReaderException(string message) : Exception(message);

public sealed class ReaderSession(ReadingQueue queue, Func<bool> announceCodeBlocks, Func<bool> voiceReady)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxDiffBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    private MarkdownDocument _document = new();

    private DiffDocument? _diff;

    // Sentence id -> the place in the diff its chunk was spoken about.
    private readonly Dictionary<int, DiffAnchor> _anchors = [];

    /// <summary>"" when empty, "stream" for text sent by Claude, otherwise the file path.</summary>
    public string Source { get; private set; } = "";

    /// <summary>The title given with the loaded diff, or null.</summary>
    public string? DiffTitle { get; private set; }

    public event Action<string>? DocumentReplaced;
    public event Action<string>? DocumentAppended;

    /// <summary>The diff HTML and title; "" and null when the diff is unloaded.</summary>
    public event Action<string, string?>? DiffReplaced;
    public event Action? ActivateRequested;

    public PipeResponse Handle(PipeRequest request)
    {
        try
        {
            switch (request.Op)
            {
                case "read_file":
                    RequireVoice();
                    OpenFile(request.Path ?? "");
                    return PipeResponse.Success($"Reading {Path.GetFileName(Source)} ({queue.Count} sentences).");
                case "speak":
                    RequireVoice();
                    var count = Speak(request.Text ?? "", request.Mode ?? "append");
                    return PipeResponse.Success($"Queued {count} sentences.");
                case "show_diff":
                    var files = ShowDiff(request.Diff ?? "", request.Title);
                    return PipeResponse.Success($"Showing diff: {files} {(files == 1 ? "file" : "files")}.");
                case "stop":
                    Stop();
                    return PipeResponse.Success("Stopped.");
                case "status":
                    return PipeResponse.Success(Status());
                case "activate":
                    ActivateRequested?.Invoke();
                    return PipeResponse.Success("Activated.");
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
            return PipeResponse.Fail($"MD Reader could not complete the request: {ex.Message}");
        }
    }

    public void OpenFile(string path)
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
        Replace(text, path);
    }

    public int Speak(string text, string mode)
    {
        if (mode is not ("append" or "replace"))
            throw new ReaderException($"Unknown mode '{mode}'. Use 'append' or 'replace'.");
        if (string.IsNullOrWhiteSpace(text)) throw new ReaderException("The text is empty.");

        if (mode == "replace" || Source == "") return Replace(text, "stream");

        var result = _document.Append(text);
        DocumentAppended?.Invoke(result.Html);
        queue.Append(result.Sentences, voiceReady());
        return result.Sentences.Count;
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
        _document = new MarkdownDocument(announceCodeBlocks());
        _anchors.Clear();
        _diff = parsed;
        DiffTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Source = "stream";
        DocumentReplaced?.Invoke("");
        DiffReplaced?.Invoke(parsed.Html, DiffTitle);
        return parsed.Files.Count;
    }

    public void Stop()
    {
        queue.Stop();
        _document = new MarkdownDocument(announceCodeBlocks());
        Source = "";
        ClearDiff();
        DocumentReplaced?.Invoke("");
    }

    public PipeResult Status()
    {
        var total = queue.Count;
        var current = total == 0 ? 0 : Math.Min(queue.CurrentIndex + 1, total);
        var state = queue.State.ToString().ToLowerInvariant();
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

    private int Replace(string markdown, string source)
    {
        _document = new MarkdownDocument(announceCodeBlocks());
        var result = _document.Append(markdown);
        Source = source;
        DocumentReplaced?.Invoke(result.Html);
        queue.Load(result.Sentences, voiceReady());
        return result.Sentences.Count;
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
            throw new ReaderException("Voice not ready. Open MD Reader and download the voice first.");
    }
}
