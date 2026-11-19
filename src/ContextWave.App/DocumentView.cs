using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ContextWave.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ContextWave.App;

public sealed class DocumentView(WebView2 webView)
{
    private bool _loaded;
    private ResolvedTheme? _theme;
    private string _visualizer = VisualizerCatalog.OffId;
    private ResolvedText? _text;

    public event Action<int>? SentenceClicked;
    public event Action<string>? FileDropped;

    /// <summary>A click in the diff: the file index, and the line index or null for the file header.</summary>
    public event Action<int, int?>? DiffClicked;

    /// <summary>A size shortcut was used in the page: +1 larger, -1 smaller, 0 back to the default.</summary>
    public event Action<int>? TextSizeRequested;

    /// <summary>The page was loaded again after a crash and is empty until given its document.</summary>
    public event Action? PageReloaded;

    public async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // The text size setting replaces the browser's own zoom, which would scale the visualiser too.
        core.Settings.IsZoomControlEnabled = false;

        core.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };

        // Injected rather than linked: the page's content policy allows no script sources.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadAsset("mermaid.min.js"));
        await LoadPageAsync(core);
        core.ProcessFailed += OnProcessFailed;
    }

    private async Task LoadPageAsync(CoreWebView2 core)
    {
        var done = new TaskCompletionSource();
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnCompleted;
            done.TrySetResult();
        }
        core.NavigationCompleted += OnCompleted;
        core.NavigateToString(LoadAsset("shell.html"));
        await done.Task;

        core.NavigationStarting += OnNavigationStarting;
        _loaded = true;
        if (_theme is not null) SetTheme(_theme);
        if (_text is not null) SetText(_text);
        SetVisualizer(_visualizer);
    }

    /// <summary>
    /// A crashed renderer leaves a blank page, so load it again. If the whole browser went, this
    /// control cannot be revived: reading carries on with no page until the app is restarted.
    /// </summary>
    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.BrowserProcessExited)) return;

        FileLog.Write($"The page stopped: {e.ProcessFailedKind} ({e.Reason}).");
        _loaded = false;
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;
        try
        {
            var core = webView.CoreWebView2;
            core.NavigationStarting -= OnNavigationStarting;
            await LoadPageAsync(core);
            PageReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write($"The page could not be loaded again: {ex}");
        }
    }

    /// <summary>The text of a file embedded in the app (see the project file).</summary>
    private static string LoadAsset(string name)
    {
        using var stream = typeof(DocumentView).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is missing from the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void SetDocument(string html) => Run($"setDoc({JsonSerializer.Serialize(html)})");

    public void Append(string html) => Run($"appendDoc({JsonSerializer.Serialize(html)})");

    public void Highlight(int sentenceId) => Run($"highlight({sentenceId})");

    /// <summary>Applies the theme to the page now, or as soon as the page has loaded.</summary>
    public void SetTheme(ResolvedTheme theme)
    {
        _theme = theme;
        // The WebView's own background shows before the page paints; match it to avoid a flash.
        var (r, g, b) = ThemeCatalog.Rgb(theme.Theme.Background);
        webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(r, g, b);
        var payload = new { dark = theme.IsDark, vars = theme.PageVariables() };
        Run($"setTheme({JsonSerializer.Serialize(payload)})");
    }

    /// <summary>Applies the text size, font, column width and line spacing now, or once the page has loaded.</summary>
    public void SetText(ResolvedText text)
    {
        _text = text;
        Run($"setText({JsonSerializer.Serialize(new { vars = text.PageVariables() })})");
    }

    /// <summary>Shows the visualiser in this style, or hides it for "off". Applies once the page has loaded.</summary>
    public void SetVisualizer(string id)
    {
        _visualizer = id;
        Run($"setVisualizer({JsonSerializer.Serialize(id)})");
    }

    /// <summary>Sends one instant of sound to the visualiser.</summary>
    public void PushAudio(AudioFrame frame)
    {
        if (!_loaded) return;
        try
        {
            webView.CoreWebView2.PostWebMessageAsJson(frame.ToJson());
        }
        catch (Exception ex) when (IsBrowserGone(ex))
        {
            // The browser process went away or the window is closing; this runs 30 times a second,
            // so there is nothing useful to log.
        }
    }

    private static bool IsBrowserGone(Exception ex) =>
        ex is InvalidOperationException or COMException or ObjectDisposedException or NullReferenceException;

    /// <summary>Shows the diff pane with this HTML, or hides it when the HTML is empty.</summary>
    public void SetDiff(string html, string? title) =>
        Run($"setDiff({JsonSerializer.Serialize(html)}, {JsonSerializer.Serialize(title ?? "")})");

    public void FocusDiff(DiffAnchor anchor) =>
        Run($"focusDiff({anchor.FileIndex}, {Js(anchor.FirstLine)}, {Js(anchor.LastLine)})");

    public void ClearDiffFocus() => Run("clearDiffFocus()");

    private static string Js(int? value) => value?.ToString() ?? "null";

    private void Run(string script)
    {
        if (!_loaded) return;
        try
        {
            _ = webView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (IsBrowserGone(ex))
        {
            // Runs inside the reader's document events, so a dead page must not stop the reading.
            FileLog.Write($"The page did not take an update: {ex.Message}");
        }
    }

    /// <summary>
    /// The page posts a sentence id, "diff:{file}:{line}" (line empty for a header), or
    /// "text:up", "text:down" or "text:reset".
    /// </summary>
    private void OnMessage(string message)
    {
        if (int.TryParse(message, out var id))
        {
            SentenceClicked?.Invoke(id);
            return;
        }

        if (message.StartsWith("text:", StringComparison.Ordinal))
        {
            int? direction = message["text:".Length..] switch
            {
                "up" => 1,
                "down" => -1,
                "reset" => 0,
                _ => null,
            };
            if (direction is { } step) TextSizeRequested?.Invoke(step);
            return;
        }

        var parts = message.Split(':');
        if (parts is not ["diff", var file, var line] || !int.TryParse(file, out var fileIndex)) return;
        if (line == "") DiffClicked?.Invoke(fileIndex, null);
        else if (int.TryParse(line, out var lineIndex)) DiffClicked?.Invoke(fileIndex, lineIndex);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.IsFile) FileDropped?.Invoke(uri.LocalPath);
        else OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }
}
