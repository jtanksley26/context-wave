using System.Text;

namespace MdReader.Core;

/// <summary>
/// One tab: a document with its own reader and reading queue. A tab is a chat's running record
/// (its replies and what it asked to have read), a file opened by the user, or the shared
/// "Claude" tab for requests that do not say which chat they come from.
/// </summary>
public sealed class ChatTab
{
    private readonly StringBuilder _html = new();

    internal ChatTab(string key, string title, ReaderSession session, ReadingQueue queue)
    {
        Key = key;
        Title = title;
        Session = session;
        Queue = queue;
        session.DocumentReplaced += html =>
        {
            _html.Clear().Append(html);
            Replacements++;
        };
        session.DocumentAppended += html => _html.Append(html);
        session.DiffReplaced += (html, title) =>
        {
            DiffHtml = html;
            DiffTitle = title;
        };
    }

    /// <summary>"session:&lt;id&gt;", "process:&lt;key&gt;", "file:&lt;path&gt;" or "claude".</summary>
    public string Key { get; internal set; }

    public string Title { get; internal set; }

    /// <summary>The Claude Code session the tab belongs to, or null.</summary>
    public string? SessionId { get; internal set; }

    /// <summary>The chat's working folder, or null.</summary>
    public string? Folder { get; internal set; }

    /// <summary>The chat's name as Claude shows it, or null until a reply has brought it.</summary>
    public string? ChatName { get; internal set; }

    public ReaderSession Session { get; }
    public ReadingQueue Queue { get; }

    /// <summary>True when something was added while the tab was in the background.</summary>
    public bool Unread { get; internal set; }

    /// <summary>Where the unread part starts, so a chat is read from there and never from halfway.</summary>
    internal int? FirstUnreadId { get; set; }

    /// <summary>
    /// The chat asked for something to be read (speak, read_file, show_diff) while another tab was
    /// reading, so it waits its turn whatever Auto Read says.
    /// </summary>
    internal bool Requested { get; set; }

    /// <summary>How many times the document has been replaced; sentence ids start again each time.</summary>
    internal int Replacements { get; private set; }

    /// <summary>The document as shown, so it can be put back when the tab is selected again.</summary>
    public string Html => _html.ToString();

    public string DiffHtml { get; private set; } = "";
    public string? DiffTitle { get; private set; }
}

/// <summary>
/// The open tabs and which one is in front. Routes each request to its chat's tab: a reply by its
/// session id, a tool call by its chat's process, which a reply has linked to a session. A request
/// whose chat has no tab yet opens one. Only the tab in front is read aloud.
/// </summary>
public sealed class ChatTabs
{
    public const string ClaudeKey = "claude";

    private readonly Func<ReadingQueue> _newQueue;
    private readonly Func<bool> _announceCodeBlocks;
    private readonly Func<bool> _voiceReady;
    private readonly Func<string> _autoRead;
    private readonly Func<string> _whenPlaying;
    private readonly Action<Action> _dispatch;
    private readonly List<ChatTab> _tabs = [];

    // A chat's process -> its current session, learned from the processes sent with replies.
    private readonly Dictionary<string, string> _sessionByProcess = [];

    // Background tabs with something unread, in the order it arrived: the order they come forward
    // in when they wait their turn.
    private readonly List<ChatTab> _line = [];

    /// <param name="autoRead">A <see cref="ReplyMode"/> (Auto Read): which chats' replies are read by themselves.</param>
    /// <param name="whenPlaying">
    /// A <see cref="WhenPlaying"/>: whether a reply or a chat's request to read something waits for
    /// what is being read, or interrupts it.
    /// </param>
    /// <param name="dispatch">Runs a callback on the thread that owns the tabs (the UI thread).</param>
    public ChatTabs(
        Func<ReadingQueue> newQueue, Func<bool> announceCodeBlocks, Func<bool> voiceReady,
        Func<string> autoRead, Func<string> whenPlaying, Action<Action> dispatch)
    {
        _newQueue = newQueue;
        _announceCodeBlocks = announceCodeBlocks;
        _voiceReady = voiceReady;
        _autoRead = autoRead;
        _whenPlaying = whenPlaying;
        _dispatch = dispatch;
    }

    private string AutoRead => ReplyMode.Normalize(_autoRead());
    private bool Interrupts => WhenPlaying.Normalize(_whenPlaying()) == WhenPlaying.Interrupt;

    public IReadOnlyList<ChatTab> Tabs => _tabs;

    /// <summary>The tab in front, which is the one shown and read; null when there are no tabs.</summary>
    public ChatTab? Active { get; private set; }

    /// <summary>A tab was opened; subscribe to its reader and queue here.</summary>
    public event Action<ChatTab>? TabOpened;

    /// <summary>A tab was opened, closed, renamed or marked read or unread.</summary>
    public event Action? TabsChanged;

    /// <summary>Another tab came to the front (or none is left).</summary>
    public event Action<ChatTab?>? ActiveChanged;

    public event Action? ActivateRequested;

    public PipeResponse Handle(PipeRequest request)
    {
        switch (request.Op)
        {
            case "activate":
                ActivateRequested?.Invoke();
                return PipeResponse.Success("Activated.");
            case "speak_reply":
                // Always a success: a skipped reply is normal, and the hook must not see an error.
                return PipeResponse.Success(Reply(request));
        }

        var existing = ToolTab(request, create: false);
        if (request.Op is not ("read_file" or "speak" or "show_diff"))
        {
            // Status and stop never open a tab or reach another chat's tab.
            if (existing is not null) return existing.Session.Handle(request);
            return request.Op switch
            {
                "status" => PipeResponse.Success(new PipeResult
                {
                    Message = "idle, sentence 0 of 0", State = "idle", Source = "",
                    CurrentSentence = 0, TotalSentences = 0, DiffFiles = 0,
                }),
                "stop" => PipeResponse.Success("Stopped."),
                _ => PipeResponse.Fail($"Unknown operation '{request.Op}'."),
            };
        }

        // A request that reads something brings its chat to the front, unless it waits its turn.
        var tab = existing ?? ToolTab(request, create: true)!;
        var previous = Active;
        var wait = !Interrupts && previous is not null && tab != previous && IsPlaying(previous);
        if (!wait) Select(tab);

        var before = tab.Queue.Count;
        var replacements = tab.Replacements;
        var response = tab.Session.Handle(request, hold: wait);
        if (!response.Ok && existing is null && tab.Html.Length == 0 && tab.DiffHtml.Length == 0)
        {
            // Nothing came of it: do not leave an empty tab behind.
            Close(tab);
            if (!wait && previous is not null && _tabs.Contains(previous)) Select(previous);
        }
        else if (wait && response.Ok)
        {
            // Read from the request itself, past replies Auto Read left unread, unless those were
            // due to be read too (All chats). Further requests from the chat follow on from it.
            MarkWaiting(tab, before, replacements, startHere: !tab.Requested && AutoRead != ReplyMode.All);
            tab.Requested = true;
        }
        return response;
    }

    /// <summary>Opens a file in a tab of its own, or reopens it in its tab, and brings that to the front.</summary>
    /// <exception cref="ReaderException">The file cannot be read; no tab is left behind for it.</exception>
    public ChatTab OpenFile(string path)
    {
        var key = $"file:{path}";
        var existing = Find(key);
        var tab = existing ?? Open(key, Path.GetFileName(path));
        var previous = Active;
        Select(tab);
        try
        {
            tab.Session.OpenFile(path);
        }
        catch (ReaderException)
        {
            if (existing is null)
            {
                Close(tab);
                if (previous is not null && _tabs.Contains(previous)) Select(previous);
            }
            throw;
        }
        return tab;
    }

    /// <summary>Brings a tab to the front. The tab that was in front stops reading but keeps its place.</summary>
    public void Select(ChatTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        var changed = tab != Active;
        if (changed && Active is { } old) old.Queue.Halt();
        Active = tab;
        var wasUnread = tab.Unread;
        MarkSeen(tab);
        if (changed) ActiveChanged?.Invoke(tab);
        if (changed || wasUnread) TabsChanged?.Invoke();
    }

    /// <summary>Closes a tab and stops its reading. The next reply from its chat opens a new one.</summary>
    public void Close(ChatTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        tab.Queue.Stop();
        _tabs.RemoveAt(index);
        _line.Remove(tab);
        if (tab == Active)
        {
            Active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
            if (Active is not null) MarkSeen(Active);
            ActiveChanged?.Invoke(Active);
        }
        TabsChanged?.Invoke();
    }

    private void MarkSeen(ChatTab tab)
    {
        tab.Unread = false;
        tab.Requested = false;
        tab.FirstUnreadId = null;
        _line.Remove(tab);
    }

    private string Reply(PipeRequest request)
    {
        // Checked before any tab is opened or brought forward.
        if (ReaderSession.ReplySkipReason(request.Text ?? "", _announceCodeBlocks()) is { } skipped) return skipped;
        var autoRead = AutoRead;
        var interrupts = Interrupts;

        var tab = request.Session is { Length: > 0 } session
            ? SessionTab(session, request.Folder, request.Name, request.Process)
            : Find(ClaudeKey) ?? Open(ClaudeKey, "Claude");
        var text = request.Text ?? "";

        if (Active is null || tab == Active)
        {
            if (Active is null) Select(tab);
            var action = autoRead == ReplyMode.Off ? ReplyAction.Hold
                : interrupts ? ReplyAction.Interrupt
                : ReplyAction.Wait;
            return tab.Session.SpeakReply(text, action);
        }

        if (autoRead == ReplyMode.All && interrupts)
        {
            Select(tab);
            return tab.Session.SpeakReply(text, ReplyAction.Interrupt);
        }

        // Background chat: hold it with a dot; with All chats it is read when its turn comes.
        var before = tab.Queue.Count;
        var replacements = tab.Replacements;
        var result = tab.Session.SpeakReply(text, ReplyAction.Hold);
        if (tab.Queue.Count != before || tab.Replacements != replacements)
        {
            MarkWaiting(tab, before, replacements);
            if (autoRead == ReplyMode.All && !IsPlaying(Active)) MoveToNext();
        }
        return result;
    }

    private static bool IsPlaying(ChatTab tab) => tab.Queue.State != ReadingState.Idle;

    /// <summary>
    /// Marks a background tab as having something unread from the sentence at
    /// <paramref name="before"/> on (or from the start, if its document was replaced), and puts it in line.
    /// </summary>
    /// <param name="startHere">Read from this addition even if older unread sentences come before it.</param>
    private void MarkWaiting(ChatTab tab, int before, int replacements, bool startHere = false)
    {
        if (tab.Replacements != replacements) tab.FirstUnreadId = tab.Queue.SentenceIdAt(0);
        else if (startHere || !tab.Unread || tab.FirstUnreadId is null) tab.FirstUnreadId = tab.Queue.SentenceIdAt(before);
        tab.Unread = true;
        if (!_line.Contains(tab)) _line.Add(tab);
        TabsChanged?.Invoke();
    }

    /// <summary>Brings forward the first tab in line whose turn it is and reads it from its first unread sentence.</summary>
    private void MoveToNext()
    {
        _line.RemoveAll(t => !_tabs.Contains(t) || t == Active || !t.Unread);
        var all = AutoRead == ReplyMode.All;
        var next = _line.FirstOrDefault(t => t.Requested || all);
        if (next is null) return;
        var start = next.FirstUnreadId;
        Select(next);
        if (start is { } id) next.Queue.JumpTo(id);
        else next.Queue.Play();
    }

    /// <summary>
    /// The tab for a session, opened if needed. A tab the chat's tool calls opened before its first
    /// reply becomes this tab.
    /// </summary>
    private ChatTab SessionTab(string session, string? folder, string? name, string? process)
    {
        if (process is { Length: > 0 }) _sessionByProcess[process] = session;

        var key = $"session:{session}";
        var tab = Find(key);
        if (tab is null && process is { Length: > 0 } && Find($"process:{process}") is { } processTab)
        {
            processTab.Key = key;
            tab = processTab;
            TabsChanged?.Invoke();
        }
        tab ??= Open(key, name is { Length: > 0 } ? name : TitleFor(folder));
        tab.SessionId = session;
        if (folder is { Length: > 0 }) tab.Folder = folder;
        // Renaming the chat in Claude renames its tab with the next reply; a reply without a name keeps it.
        if (name is { Length: > 0 }) tab.ChatName = name;
        var title = tab.ChatName ?? TitleFor(tab.Folder);
        if (tab.Title != title)
        {
            tab.Title = title;
            TabsChanged?.Invoke();
        }
        return tab;
    }

    /// <summary>The tab a tool call belongs to: its chat's, else the tab in front, else the "Claude" tab.</summary>
    private ChatTab? ToolTab(PipeRequest request, bool create)
    {
        if (request.Process is { Length: > 0 } process)
        {
            var session = _sessionByProcess.GetValueOrDefault(process);
            var key = session is null ? $"process:{process}" : $"session:{session}";
            var tab = Find(key);
            if (tab is null && create)
            {
                tab = Open(key, TitleFor(request.Folder));
                tab.SessionId = session;
                tab.Folder = request.Folder;
            }
            return tab;
        }
        return Active ?? Find(ClaudeKey) ?? (create ? Open(ClaudeKey, "Claude") : null);
    }

    private ChatTab Open(string key, string title)
    {
        var queue = _newQueue();
        var session = new ReaderSession(queue, _announceCodeBlocks, _voiceReady);
        var tab = new ChatTab(key, title, session, queue);
        _tabs.Add(tab);
        queue.Finished += () => _dispatch(() => OnFinished(tab));
        TabOpened?.Invoke(tab);
        TabsChanged?.Invoke();
        return tab;
    }

    /// <summary>The next tab in line comes forward once the front tab, and replies queued onto it, are read.</summary>
    private void OnFinished(ChatTab tab)
    {
        if (tab != Active || tab.Queue.State != ReadingState.Idle) return;
        MoveToNext();
    }

    private ChatTab? Find(string key) => _tabs.FirstOrDefault(t => t.Key == key);

    private static string TitleFor(string? folder)
    {
        var name = folder is { Length: > 0 } ? Path.GetFileName(folder.TrimEnd('\\', '/')) : "";
        return name.Length > 0 ? name : "Claude";
    }
}
