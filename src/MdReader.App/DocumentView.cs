using System.Diagnostics;
using System.Text.Json;
using MdReader.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MdReader.App;

public sealed class DocumentView(WebView2 webView)
{
    private const string ShellHtml = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
        <style>
          :root { color-scheme: light dark; --bg:#ffffff; --fg:#1f2328; --hl:#fff3a3; --line:#d0d7de; --code:#f6f8fa; }
          @media (prefers-color-scheme: dark) {
            :root { --bg:#1e1e1e; --fg:#e6e6e6; --hl:#5c4b00; --line:#444444; --code:#2a2a2a; }
          }
          body { background:var(--bg); color:var(--fg); font:17px/1.65 "Segoe UI",sans-serif;
                 max-width:760px; margin:0 auto; padding:24px 32px 40vh; }
          [data-sid] { cursor:pointer; border-radius:3px; }
          .speaking { background:var(--hl); }
          pre { background:var(--code); padding:12px; overflow:auto; border-radius:6px; }
          code { font-family:Consolas,monospace; font-size:.92em; }
          pre code[data-sid] { display:block; }
          table { border-collapse:collapse; }
          th, td { border:1px solid var(--line); padding:4px 10px; }
          blockquote { border-left:4px solid var(--line); margin-left:0; padding-left:16px; }
          a { color:inherit; }
          #empty { opacity:.6; margin-top:30vh; text-align:center; }
        </style>
        </head>
        <body>
        <div id="doc"></div>
        <div id="empty">Open a markdown file, drop one here, or ask Claude to read to you.</div>
        <script>
          const doc = document.getElementById('doc');
          const empty = document.getElementById('empty');
          function setDoc(html) {
            doc.innerHTML = html;
            empty.style.display = html ? 'none' : '';
            window.scrollTo(0, 0);
          }
          function appendDoc(html) {
            doc.insertAdjacentHTML('beforeend', html);
            empty.style.display = 'none';
          }
          function highlight(id) {
            document.querySelectorAll('.speaking').forEach(e => e.classList.remove('speaking'));
            const parts = document.querySelectorAll('[data-sid="' + id + '"]');
            parts.forEach(e => e.classList.add('speaking'));
            if (parts.length) parts[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
          }
          document.addEventListener('click', e => {
            if (e.target.closest('a')) return;
            const el = e.target.closest('[data-sid]');
            if (el) window.chrome.webview.postMessage(el.dataset.sid);
          });
        </script>
        </body>
        </html>
        """;

    private bool _loaded;

    public event Action<int>? SentenceClicked;
    public event Action<string>? FileDropped;

    public async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.WebMessageReceived += (_, e) =>
        {
            if (int.TryParse(e.TryGetWebMessageAsString(), out var id)) SentenceClicked?.Invoke(id);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };

        var done = new TaskCompletionSource();
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnCompleted;
            done.TrySetResult();
        }
        core.NavigationCompleted += OnCompleted;
        core.NavigateToString(ShellHtml);
        await done.Task;

        core.NavigationStarting += OnNavigationStarting;
        _loaded = true;
    }

    public void SetDocument(string html) => Run($"setDoc({JsonSerializer.Serialize(html)})");

    public void Append(string html) => Run($"appendDoc({JsonSerializer.Serialize(html)})");

    public void Highlight(int sentenceId) => Run($"highlight({sentenceId})");

    private void Run(string script)
    {
        if (_loaded) _ = webView.CoreWebView2.ExecuteScriptAsync(script);
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
