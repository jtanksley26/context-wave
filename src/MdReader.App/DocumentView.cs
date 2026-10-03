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
          :root { color-scheme: light dark; --bg:#ffffff; --fg:#1f2328; --hl:#fff3a3; --line:#d0d7de; --code:#f6f8fa;
                  --add:#e6ffec; --del:#ffebe9; --focus:#bf8700; --focusbg:rgba(255,212,0,.22); }
          @media (prefers-color-scheme: dark) {
            :root { --bg:#1e1e1e; --fg:#e6e6e6; --hl:#5c4b00; --line:#444444; --code:#2a2a2a;
                    --add:#12361f; --del:#4a1d1d; --focus:#e3b341; --focusbg:rgba(227,179,65,.2); }
          }
          html { height:100%; }
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

          /* Without a diff the page is the single centred column above. */
          #diff, #divider { display:none; }
          body.split { max-width:none; margin:0; padding:0; height:100%; display:flex; overflow:hidden; }
          body.split #diff { display:block; flex:0 0 var(--diffw, 55%); min-width:0; overflow:auto;
                             font:13px/1.5 Consolas,monospace; }
          body.split #divider { display:block; flex:0 0 6px; cursor:col-resize; background:var(--line); }
          body.split #text { flex:1 1 0; min-width:0; overflow:auto; padding:24px 32px 40vh; }
          body.split #empty { display:none; }

          #diffTitle { padding:8px 10px; font:600 14px "Segoe UI",sans-serif; }
          #diffTitle:empty { display:none; }
          .df { min-width:max-content; margin-bottom:18px; }
          .dfh { position:sticky; top:0; padding:6px 10px; font-weight:600; cursor:pointer;
                 background:var(--code); border-top:1px solid var(--line); border-bottom:1px solid var(--line); }
          .dfk { font-weight:400; opacity:.7; margin-right:8px; }
          .dbin { padding:6px 10px; opacity:.7; }
          .dl { display:flex; white-space:pre; cursor:pointer; }
          .dn { flex:0 0 5ch; text-align:right; padding-right:8px; opacity:.55; user-select:none; }
          .dt { flex:1 0 auto; padding:0 12px 0 4px; tab-size:4; }
          .dl.add { background-color:var(--add); }
          .dl.del { background-color:var(--del); }
          .dl.hunk { background-color:var(--code); opacity:.75; }
          .focused { box-shadow:inset 4px 0 0 var(--focus);
                     background-image:linear-gradient(var(--focusbg), var(--focusbg)); }
          .focus-label { font:12px Consolas,monospace; opacity:.7; margin:22px 0 -10px; }
        </style>
        </head>
        <body>
        <div id="diff"><div id="diffTitle"></div><div id="diffBody"></div></div>
        <div id="divider"></div>
        <div id="text">
          <div id="doc"></div>
          <div id="empty">Open a markdown file, drop one here, or ask Claude to read to you.</div>
        </div>
        <script>
          const doc = document.getElementById('doc');
          const empty = document.getElementById('empty');
          const textPane = document.getElementById('text');
          const diffPane = document.getElementById('diff');
          const diffTitle = document.getElementById('diffTitle');
          const diffBody = document.getElementById('diffBody');
          const divider = document.getElementById('divider');

          function setDoc(html) {
            doc.innerHTML = html;
            empty.style.display = html ? 'none' : '';
            window.scrollTo(0, 0);
            textPane.scrollTop = 0;
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

          function setDiff(html, title) {
            diffBody.innerHTML = html;
            diffTitle.textContent = title;
            document.body.classList.toggle('split', html !== '');
            diffPane.scrollTop = 0;
            diffPane.scrollLeft = 0;
          }
          function clearDiffFocus() {
            diffBody.querySelectorAll('.focused').forEach(e => e.classList.remove('focused'));
          }
          // first and last are data-line values, or null to focus the file header.
          function focusDiff(file, first, last) {
            clearDiffFocus();
            let target = null;
            if (first === null) {
              target = diffBody.querySelector('[data-file="' + file + '"] .dfh');
              if (target) target.classList.add('focused');
            } else {
              for (let i = first; i <= last; i++) {
                const row = diffBody.querySelector('.dl[data-line="' + i + '"]');
                if (!row) continue;
                row.classList.add('focused');
                target = target || row;
              }
            }
            if (target) target.scrollIntoView({ behavior: 'smooth', block: first === null ? 'start' : 'center' });
          }

          document.addEventListener('click', e => {
            if (e.target.closest('a')) return;
            const row = e.target.closest('.dl');
            const head = e.target.closest('.dfh');
            if (row || head) {
              const section = (row || head).closest('[data-file]');
              window.chrome.webview.postMessage('diff:' + section.dataset.file + ':' + (row ? row.dataset.line : ''));
              return;
            }
            const el = e.target.closest('[data-sid]');
            if (el) window.chrome.webview.postMessage(el.dataset.sid);
          });

          divider.addEventListener('pointerdown', e => {
            divider.setPointerCapture(e.pointerId);
            e.preventDefault();
          });
          divider.addEventListener('pointermove', e => {
            if (!divider.hasPointerCapture(e.pointerId)) return;
            const percent = Math.min(75, Math.max(25, e.clientX / window.innerWidth * 100));
            document.body.style.setProperty('--diffw', percent + '%');
          });
        </script>
        </body>
        </html>
        """;

    private bool _loaded;

    public event Action<int>? SentenceClicked;
    public event Action<string>? FileDropped;

    /// <summary>A click in the diff: the file index, and the line index or null for the file header.</summary>
    public event Action<int, int?>? DiffClicked;

    public async Task InitializeAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
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

    /// <summary>Shows the diff pane with this HTML, or hides it when the HTML is empty.</summary>
    public void SetDiff(string html, string? title) =>
        Run($"setDiff({JsonSerializer.Serialize(html)}, {JsonSerializer.Serialize(title ?? "")})");

    public void FocusDiff(DiffAnchor anchor) =>
        Run($"focusDiff({anchor.FileIndex}, {Js(anchor.FirstLine)}, {Js(anchor.LastLine)})");

    public void ClearDiffFocus() => Run("clearDiffFocus()");

    private static string Js(int? value) => value?.ToString() ?? "null";

    private void Run(string script)
    {
        if (_loaded) _ = webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>The page posts a sentence id, or "diff:{file}:{line}" with the line empty for a header.</summary>
    private void OnMessage(string message)
    {
        if (int.TryParse(message, out var id))
        {
            SentenceClicked?.Invoke(id);
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
