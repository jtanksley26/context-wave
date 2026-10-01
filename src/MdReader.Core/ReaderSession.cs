namespace MdReader.Core;

public sealed class ReaderException(string message) : Exception(message);

public sealed class ReaderSession(ReadingQueue queue, Func<bool> announceCodeBlocks, Func<bool> voiceReady)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };

    private MarkdownDocument _document = new();

    /// <summary>"" when empty, "stream" for text sent by Claude, otherwise the file path.</summary>
    public string Source { get; private set; } = "";

    public event Action<string>? DocumentReplaced;
    public event Action<string>? DocumentAppended;
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

    public void Stop()
    {
        queue.Stop();
        _document = new MarkdownDocument(announceCodeBlocks());
        Source = "";
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

    private void RequireVoice()
    {
        if (!voiceReady())
            throw new ReaderException("Voice not ready. Open MD Reader and download the voice first.");
    }
}
