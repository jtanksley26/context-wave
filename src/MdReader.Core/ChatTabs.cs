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
        session.DocumentReplaced += html => _html.Clear().Append(html);
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

    public ReaderSession Session { get; }
    public ReadingQueue Queue { get; }

    /// <summary>True when something was added while the tab was in the background.</summary>
    public bool Unread { get; internal set; }

    /// <summary>Where the unread part starts, so a chat is read from there and never from halfway.</summary>
    internal int? FirstUnreadId { get; set; }

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
    private readonly Func<string> _replyMode;
    private readonly Func<string> _focus;
    private readonly Action<Action> _dispatch;
    private readonly List<ChatTab> _tabs = [];

    // A chat's process -> its current session, learned from the processes sent with replies.
    private readonly Dictionary<string, string> _sessionByProcess = [];

    // Background tabs with unread replies, in the order their first unread reply arrived: the
    // order "Move to the next chat when done" brings them forward.
    private readonly List<ChatTab> _line = [];

    /// <param name="replyMode">A <see cref="ReplyMode"/>: how the tab in front reads a new reply.</param>
    /// <param name="focus">A <see cref="TabFocus"/>: whether another chat's reply brings its tab forward.</param>
    /// <param name="dispatch">Runs a callback on the thread that owns the tabs (the UI thread).</param>
    public ChatTabs(
        Func<ReadingQueue> newQueue, Func<bool> announceCodeBlocks, Func<bool> voiceReady,
        Func<string> replyMode, Func<string> focus, Action<Action>? dispatch = null)
    {
        _newQueue = newQueue;
        _announceCodeBlocks = announceCodeBlocks;
        _voiceReady = voiceReady;
        _replyMode = replyMode;
        _focus = focus;
        _dispatch = dispatch ?? (action => action());
    }

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
        // A request that reads something brings its chat to the front, so the one voice reads it.
        if (request.Op is not ("read_file" or "speak" or "show_diff"))
        {
            // Asking about or stopping a chat that has no tab does not open one.
            var target = existing ?? Active;
            if (target is not null) return target.Session.Handle(request);
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

        var tab = existing ?? ToolTab(request, create: true)!;
        var previous = Active;
        Select(tab);

        var response = tab.Session.Handle(request);
        if (!response.Ok && existing is null && tab.Html.Length == 0 && tab.DiffHtml.Length == 0)
        {
            // Nothing came of it: do not leave an empty tab behind.
            Close(tab);
            if (previous is not null && _tabs.Contains(previous)) Select(previous);
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
        // Seen now: it no longer waits its turn.
        tab.Unread = false;
        tab.FirstUnreadId = null;
        _line.Remove(tab);
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
            if (Active is not null)
            {
                Active.Unread = false;
                Active.FirstUnreadId = null;
                _line.Remove(Active);
            }
            ActiveChanged?.Invoke(Active);
        }
        TabsChanged?.Invoke();
    }

    private string Reply(PipeRequest request)
    {
        // Checked before any tab is opened or brought forward.
        if (ReaderSession.ReplySkipReason(request.Text ?? "", _announceCodeBlocks()) is { } skipped) return skipped;
        var reading = ReplyMode.Normalize(_replyMode()) != ReplyMode.Off;
        var focus = TabFocus.Normalize(_focus());

        var opened = _tabs.Count;
        var tab = request.Session is { Length: > 0 } session
            ? SessionTab(session, request.Folder, request.Processes ?? [])
            : Find(ClaudeKey) ?? Open(ClaudeKey, "Claude");
        var isNew = _tabs.Count > opened;
        var text = request.Text ?? "";

        string result;
        if (Active is null || tab == Active)
        {
            // The tab in front reads it as the reply mode says.
            if (Active is null) Select(tab);
            result = tab.Session.SpeakReply(text);
        }
        else if (reading && focus == TabFocus.Newest)
        {
            // Only the new reply is read; anything older in that tab stays where it is.
            Select(tab);
            result = tab.Session.SpeakReply(text);
        }
        else
        {
            var before = tab.Queue.Count;
            result = tab.Session.SpeakReply(text, hold: true);
            if (tab.Queue.Count > before)
            {
                if (!tab.Unread) tab.FirstUnreadId = tab.Queue.SentenceIdAt(before);
                tab.Unread = true;
                if (!_line.Contains(tab)) _line.Add(tab);
                TabsChanged?.Invoke();
                // Nothing left to read in front: move on now rather than waiting for a finish.
                if (reading && focus == TabFocus.Next && IsDone(Active)) MoveToNext();
            }
        }

        // A reply that was skipped (empty, too long, nothing to read) leaves no empty tab behind.
        if (isNew && tab.Html.Length == 0) Close(tab);
        return result;
    }

    /// <summary>A tab is done when it is not reading and nothing in it is left unread.</summary>
    private static bool IsDone(ChatTab tab) =>
        tab.Queue.State == ReadingState.Idle && tab.Queue.CurrentIndex >= tab.Queue.Count;

    /// <summary>Brings the first chat in line forward and reads it from its first unread reply.</summary>
    private void MoveToNext()
    {
        while (_line.Count > 0)
        {
            var next = _line[0];
            _line.RemoveAt(0);
            if (!_tabs.Contains(next) || next == Active || !next.Unread) continue;
            var start = next.FirstUnreadId;
            Select(next);
            if (start is { } id) next.Queue.JumpTo(id);
            else next.Queue.Play();
            return;
        }
    }

    /// <summary>
    /// The tab for a session, opened if needed. The chat's processes are linked to the session so
    /// its tool calls find this tab, and a tab its tool calls opened before its first reply becomes
    /// this tab.
    /// </summary>
    private ChatTab SessionTab(string session, string? folder, IReadOnlyList<string> processes)
    {
        foreach (var process in processes) _sessionByProcess[process] = session;

        var key = $"session:{session}";
        var tab = Find(key);
        if (tab is null)
        {
            tab = processes.Select(process => Find($"process:{process}")).FirstOrDefault(t => t is not null);
            if (tab is not null)
            {
                tab.Key = key;
                TabsChanged?.Invoke();
            }
        }
        tab ??= Open(key, TitleFor(folder));
        tab.SessionId = session;
        if (folder is { Length: > 0 } && tab.Folder != folder)
        {
            tab.Folder = folder;
            tab.Title = TitleFor(folder);
            TabsChanged?.Invoke();
        }
        return tab;
    }

    /// <summary>The tab a tool call belongs to: its chat's, else the tab in front, else the "Claude" tab.</summary>
    private ChatTab? ToolTab(PipeRequest request, bool create)
    {
        if (request.Process is { Length: > 0 } process)
        {
            if (_sessionByProcess.TryGetValue(process, out var session))
            {
                var key = $"session:{session}";
                var tab = Find(key);
                if (tab is null && create)
                {
                    tab = Open(key, TitleFor(request.Folder));
                    tab.SessionId = session;
                    tab.Folder = request.Folder;
                }
                return tab;
            }
            var processKey = $"process:{process}";
            var processTab = Find(processKey);
            if (processTab is null && create)
            {
                processTab = Open(processKey, TitleFor(request.Folder));
                processTab.Folder = request.Folder;
            }
            return processTab;
        }
        return Active ?? Find(ClaudeKey) ?? (create ? Open(ClaudeKey, "Claude") : null);
    }

    private ChatTab Open(string key, string title)
    {
        var queue = _newQueue();
        var session = new ReaderSession(queue, _announceCodeBlocks, _voiceReady, _replyMode);
        var tab = new ChatTab(key, title, session, queue);
        _tabs.Add(tab);
        queue.Finished += () => _dispatch(() => OnFinished(tab));
        TabOpened?.Invoke(tab);
        TabsChanged?.Invoke();
        return tab;
    }

    /// <summary>
    /// With "Move to the next chat when done": when the tab in front stops at the end of what it was
    /// reading, it first reads anything that arrived in it meanwhile, and only then do the chats in
    /// line come forward.
    /// </summary>
    private void OnFinished(ChatTab tab)
    {
        if (tab != Active || tab.Queue.State != ReadingState.Idle) return;
        if (ReplyMode.Normalize(_replyMode()) == ReplyMode.Off || TabFocus.Normalize(_focus()) != TabFocus.Next) return;
        if (tab.Queue.CurrentIndex < tab.Queue.Count)
        {
            tab.Queue.Play();
            return;
        }
        MoveToNext();
    }

    private ChatTab? Find(string key) => _tabs.FirstOrDefault(t => t.Key == key);

    private static string TitleFor(string? folder)
    {
        var name = folder is { Length: > 0 } ? Path.GetFileName(folder.TrimEnd('\\', '/')) : "";
        return name.Length > 0 ? name : "Claude";
    }
}
