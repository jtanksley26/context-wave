using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
          /* The Light theme; setTheme() replaces every one of these. */
          :root { color-scheme: light; --bg:#ffffff; --fg:#1f2328; --hl:#fff3a3; --hlfg:#1f2328;
                  --line:#d0d7de; --code:#f6f8fa; --add:#e6ffec; --del:#ffebe9;
                  --focus:#9a6700; --focusbg:rgba(154,103,0,0.22); }
          html { height:100%; }
          /* Today's text settings; setText() replaces every one of these. */
          :root { --size:17px; --font:"Segoe UI",sans-serif; --col:44.7em; --lh:1.65; }
          body { background:var(--bg); color:var(--fg); font:var(--size)/var(--lh) var(--font);
                 margin:0; padding:24px 32px 40vh; }
          /* The column is capped on the document, not the body, so the visualiser can span the pane. */
          #doc, #empty { max-width:var(--col); margin-left:auto; margin-right:auto; }
          [data-sid] { cursor:pointer; border-radius:3px; }
          .speaking { background:var(--hl); color:var(--hlfg); }
          pre { background:var(--code); padding:12px; overflow:auto; border-radius:6px; }
          code { font-family:Consolas,monospace; font-size:.92em; }
          pre code[data-sid] { display:block; }
          /* A mermaid block once it is drawn; the code is kept in data-source to redraw on a theme or text change. */
          .diagram { margin:1em 0; padding:12px; text-align:center; overflow:auto; border-radius:6px; }
          .diagram svg { vertical-align:top; }
          /* Where mermaid measures a diagram as it draws it: out of the layout, so the text does not move. */
          #scratch { position:fixed; left:0; top:0; width:100%; visibility:hidden; pointer-events:none; }
          table { border-collapse:collapse; }
          th, td { border:1px solid var(--line); padding:4px 10px; }
          blockquote { border-left:4px solid var(--line); margin-left:0; padding-left:16px; }
          a { color:inherit; }
          #empty { opacity:.6; margin-top:30vh; text-align:center; }

          /* The visualiser sits flush at the top of the text pane and stays there while text scrolls. */
          #viz { position:sticky; top:0; z-index:2; height:140px; margin:-24px -32px 16px;
                 background:var(--bg); border-bottom:1px solid var(--line); }
          #viz.off { display:none; }
          /* In the split layout the text pane scrolls, and sticky is measured from inside its padding. */
          body.split #viz { top:-24px; }
          #viz canvas { display:block; width:100%; height:100%; }

          /* Without a diff the page is the single centred column above. */
          #diff, #divider { display:none; }
          body.split { max-width:none; margin:0; padding:0; height:100%; display:flex; overflow:hidden; }
          body.split #diff { display:block; flex:0 0 var(--diffw, 55%); min-width:0; overflow:auto;
                             font:calc(var(--size) * 0.765)/1.5 Consolas,monospace; }
          body.split #divider { display:block; flex:0 0 6px; cursor:col-resize; background:var(--line); }
          body.split #text { flex:1 1 0; min-width:0; overflow:auto; padding:24px 32px 40vh; }
          body.split #empty { display:none; }

          #diffTitle { padding:8px 10px; font:600 14px "Segoe UI",sans-serif; }
          #diffTitle:empty { display:none; }
          .df { min-width:max-content; margin-bottom:18px; }
          /* z-index: the dimmed line numbers below would otherwise paint over the stuck header. */
          .dfh { position:sticky; top:0; z-index:1; padding:6px 10px; font-weight:600; cursor:pointer;
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
          .focus-label { font:calc(var(--size) * 0.7) Consolas,monospace; opacity:.7; margin:22px 0 -10px; }
        </style>
        </head>
        <body>
        <div id="diff"><div id="diffTitle"></div><div id="diffBody"></div></div>
        <div id="divider"></div>
        <div id="text">
          <div id="viz" class="off"><canvas></canvas></div>
          <div id="doc"></div>
          <div id="empty">Open a markdown file, drop one here, or ask Claude to read to you.</div>
        </div>
        <div id="scratch"></div>
        <script>
          const doc = document.getElementById('doc');
          const empty = document.getElementById('empty');
          const textPane = document.getElementById('text');
          const diffPane = document.getElementById('diff');
          const diffTitle = document.getElementById('diffTitle');
          const diffBody = document.getElementById('diffBody');
          const scratch = document.getElementById('scratch');
          const divider = document.getElementById('divider');

          function setDoc(html) {
            doc.innerHTML = html;
            empty.style.display = html ? 'none' : '';
            window.scrollTo(0, 0);
            textPane.scrollTop = 0;
            drawDiagrams();
          }
          function appendDoc(html) {
            doc.insertAdjacentHTML('beforeend', html);
            empty.style.display = 'none';
            drawDiagrams();
          }

          // ---- Diagrams ----
          // mermaid is injected by the app before this page loads; without it the blocks stay as code.
          let diagramCount = 0;
          function drawDiagrams() {
            if (!window.mermaid) return;
            for (const code of doc.querySelectorAll('pre > code[class~="language-mermaid" i]')) {
              const box = document.createElement('div');
              box.className = 'diagram';
              box.dataset.source = code.textContent;
              if (code.dataset.sid) box.dataset.sid = code.dataset.sid;
              const pre = code.parentElement;
              // Swap the block out only once it has drawn, so a bad diagram is still shown as code.
              drawDiagram(box).then(ok => {
                if (!ok || !pre.isConnected) return;
                // The block may have started being read aloud while it was drawn.
                box.classList.toggle('speaking', code.classList.contains('speaking'));
                pre.replaceWith(box);
                fitDiagram(box);
              });
              code.removeAttribute('class'); // so a later append does not draw it again
            }
          }
          // Labels use the reading text's size and font, so a diagram is redrawn when either changes.
          // A flowchart too wide for the column is tried again with tighter spacing, then with its labels wrapped
          // tighter too, and the layout that has to shrink least is kept, so its labels stay as close to the text
          // size as they can. Sizes are px at mermaid's 16px text and scale with the text size.
          // Mermaid's default ELK layout ignores spacing settings and keeps every box at least 120px wide, so the
          // tight layouts switch to dagre, which honours them.
          const NORMAL = { wrap: 200 }; // mermaid's own layout and spacing
          const TIGHT = { layout: 'dagre', minNodeWidth: 0, nodeSpacing: 20, rankSpacing: 25, padding: 6 };
          const LAYOUTS = [NORMAL, { ...TIGHT, wrap: 200 }, { ...TIGHT, wrap: 140 }, { ...TIGHT, wrap: 100 }, { ...TIGHT, wrap: 70 }];
          // One diagram at a time: mermaid.initialize applies at once but mermaid.render runs later from a queue,
          // so overlapping draws would render with each other's settings.
          // A draw reads the text settings and width when it starts, so one that is still waiting already covers
          // any change made since it was asked for, and is not queued again.
          let drawing = Promise.resolve();
          const waiting = new WeakMap();
          function drawDiagram(box) {
            if (waiting.has(box)) return waiting.get(box);
            const run = drawing.then(() => {
              waiting.delete(box);
              return drawDiagramNow(box);
            });
            waiting.set(box, run);
            drawing = run.catch(() => {});
            return run;
          }
          async function drawDiagramNow(box) {
            const css = getComputedStyle(document.documentElement);
            const font = css.getPropertyValue('--font').trim();
            const size = css.getPropertyValue('--size').trim();
            const k = (parseFloat(size) || 16) / 16;
            const room = doc.clientWidth - 24; // the box's padding
            const layouts = FLOWCHART.test(box.dataset.source) ? LAYOUTS : [NORMAL];
            let best = null;
            for (const layout of layouts) {
              const flowchart = {};
              for (const key in layout) {
                if (key !== 'layout') flowchart[key === 'wrap' ? 'wrappingWidth' : key] = Math.round(layout[key] * k);
              }
              const svg = await renderDiagram(box.dataset.source, font, size, flowchart, layout.layout);
              if (svg === null) return false;
              const width = parseFloat((svg.match(/viewBox="[-\d.]+ [-\d.]+ ([\d.]+)/) || [])[1]) || room;
              const scale = Math.min(1, room / width);
              if (!best || scale > best.scale) best = { svg, scale };
              if (scale >= 1) break;
            }
            box.innerHTML = best.svg;
            if (box.isConnected) fitDiagram(box);
            return true;
          }
          // mermaid.initialize merges into the settings left by earlier calls, so every call restates the layout
          // settings, starting from mermaid's own defaults as they were before the first call.
          // Copied out now, as primitives, in case getConfig hands back the live settings.
          const MERMAID_DEFAULTS = window.mermaid ? mermaid.mermaidAPI.getConfig() : {};
          const DEFAULT_LAYOUT = MERMAID_DEFAULTS.layout;
          const DEFAULT_FLOWCHART = Object.fromEntries(['minNodeWidth', 'nodeSpacing', 'rankSpacing', 'padding']
            .map(key => [key, MERMAID_DEFAULTS.flowchart?.[key]]).filter(([, value]) => value !== undefined));
          // engine is a mermaid layout name, or undefined for mermaid's default.
          async function renderDiagram(source, font, size, flowchart, engine) {
            const id = 'mermaid-' + ++diagramCount;
            try {
              mermaid.initialize({
                startOnLoad: false,
                securityLevel: 'strict',
                theme: document.documentElement.style.colorScheme === 'dark' ? 'dark' : 'default',
                fontFamily: font,
                themeVariables: { fontFamily: font, fontSize: size },
                layout: engine || DEFAULT_LAYOUT,
                flowchart: { ...DEFAULT_FLOWCHART, ...flowchart }
              });
              // Drawn in #scratch: mermaid would otherwise add its scratch element to the body, which in the
              // split layout squeezes the text pane, and the width change would start another draw.
              return (await mermaid.render(id, source, scratch)).svg;
            } catch {
              // A failed render can leave its scratch element behind.
              document.getElementById('d' + id)?.remove();
              return null;
            }
          }
          // A wide diagram shrinks to fit the column, but no further than this; past it the box scrolls.
          // Flowcharts, in any direction, are the exception: they shrink as far as needed and never scroll.
          const MIN_DIAGRAM_SCALE = 0.75;
          // The diagram type is the first word after any front matter, directives and comments.
          const FLOWCHART = /^(?:\s*---\s*\n[\s\S]*?\n\s*---\s*\n)?(?:\s*%%\{[\s\S]*?\}%%|\s*%%[^\n]*)*\s*(?:flowchart|graph)\b/i;
          function fitDiagram(box) {
            const svg = box.querySelector('svg');
            const view = svg && svg.viewBox.baseVal;
            if (!view || !view.width) return;
            const pad = getComputedStyle(box);
            const room = box.clientWidth - parseFloat(pad.paddingLeft) - parseFloat(pad.paddingRight);
            const floor = FLOWCHART.test(box.dataset.source) ? 0 : MIN_DIAGRAM_SCALE;
            const scale = Math.min(1, Math.max(floor, room / view.width));
            svg.style.maxWidth = 'none'; // mermaid's own cap would shrink it all the way
            // Rounded down so a diagram scaled to the column does not overflow it by a fraction of a pixel.
            svg.setAttribute('width', Math.floor(view.width * scale));
            svg.setAttribute('height', Math.floor(view.height * scale));
          }
          function redrawDiagrams() {
            if (window.mermaid) doc.querySelectorAll('.diagram').forEach(drawDiagram);
          }
          // Covers window resizes, the column width setting and the diff divider. Rescaling is immediate; a
          // flowchart's label wrapping is chosen again once the width has settled.
          let docWidth = 0, rewrapTimer = 0;
          new ResizeObserver(() => {
            doc.querySelectorAll('.diagram').forEach(fitDiagram);
            if (doc.clientWidth === docWidth) return;
            docWidth = doc.clientWidth;
            clearTimeout(rewrapTimer);
            rewrapTimer = setTimeout(() => {
              if (!window.mermaid) return;
              doc.querySelectorAll('.diagram').forEach(box => { if (FLOWCHART.test(box.dataset.source)) drawDiagram(box); });
            }, 300);
          }).observe(doc);
          function highlight(id) {
            document.querySelectorAll('.speaking').forEach(e => e.classList.remove('speaking'));
            const parts = document.querySelectorAll('[data-sid="' + id + '"]');
            parts.forEach(e => e.classList.add('speaking'));
            if (parts.length) parts[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
          }

          // text.vars maps a variable name (without "--") to its value: size, font, col, lh.
          function setText(text) {
            const root = document.documentElement;
            const before = root.style.getPropertyValue('--size') + root.style.getPropertyValue('--font');
            for (const name in text.vars) root.style.setProperty('--' + name, text.vars[name]);
            if (root.style.getPropertyValue('--size') + root.style.getPropertyValue('--font') !== before) redrawDiagrams();
          }

          // theme.vars maps a variable name (without "--") to its value.
          function setTheme(theme) {
            const root = document.documentElement;
            for (const name in theme.vars) root.style.setProperty('--' + name, theme.vars[name]);
            const wasDark = root.style.colorScheme === 'dark';
            root.style.colorScheme = theme.dark ? 'dark' : 'light';
            if (wasDark !== theme.dark) redrawDiagrams();
            wake();
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
              // Scroll to the section: a sticky header reports where it is stuck, not where the file starts.
              target = diffBody.querySelector('[data-file="' + file + '"]');
              if (target) target.querySelector('.dfh').classList.add('focused');
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

          // Ctrl with plus, minus or 0, and Ctrl with the wheel, change the text size setting.
          document.addEventListener('keydown', e => {
            if (!e.ctrlKey || e.altKey) return;
            const step = e.key === '+' || e.key === '=' ? 'up' : e.key === '-' ? 'down' : e.key === '0' ? 'reset' : null;
            if (!step) return;
            e.preventDefault();
            window.chrome.webview.postMessage('text:' + step);
          });
          let lastWheelStep = 0;
          document.addEventListener('wheel', e => {
            if (!e.ctrlKey) return;
            e.preventDefault();
            if (e.deltaY === 0) return; // a sideways tilt
            const now = performance.now();
            if (now - lastWheelStep < 120) return;
            lastWheelStep = now;
            window.chrome.webview.postMessage('text:' + (e.deltaY < 0 ? 'up' : 'down'));
          }, { passive: false });

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

          // ---- Voice visualiser ----
          const viz = document.getElementById('viz');
          const canvas = viz.querySelector('canvas');
          const ctx = canvas.getContext('2d');
          const BANDS = 16, WAVE = 64, REST_MS = 3000, TAU = Math.PI * 2;
          const zeros = n => new Array(n).fill(0);
          const vz = {
            style: 'off',
            target: { level: 0, bands: zeros(BANDS), wave: zeros(WAVE) },
            level: 0, bands: zeros(BANDS), wave: zeros(WAVE), peaks: zeros(BANDS),
            rings: [], dots: [], trend: 0, spin: 0, awakeUntil: 0, raf: 0, last: 0, w: 0, h: 0
          };

          function setVisualizer(style) {
            vz.style = style;
            viz.classList.toggle('off', style === 'off');
            vz.rings = [];
            if (style === 'off') {
              cancelAnimationFrame(vz.raf);
              vz.raf = 0;
              return;
            }
            wake();
          }

          // frame is { level, bands[16], wave[64] } from the app, about 30 times a second.
          function pushAudio(frame) {
            if (vz.style === 'off') return;
            vz.target = frame;
            if (frame.level > 0.02) vz.awakeUntil = performance.now() + REST_MS;
            wake();
          }

          // Runs the loop for at least a moment, so a change is painted even at rest.
          function wake() {
            if (vz.style === 'off') return;
            const now = performance.now();
            vz.awakeUntil = Math.max(vz.awakeUntil, now + 600);
            if (!vz.raf) {
              vz.last = now;
              vz.raf = requestAnimationFrame(tick);
            }
          }

          function sizeCanvas() {
            const box = viz.getBoundingClientRect();
            const ratio = window.devicePixelRatio || 1;
            vz.w = box.width;
            vz.h = box.height;
            const width = Math.round(box.width * ratio), height = Math.round(box.height * ratio);
            if (canvas.width !== width || canvas.height !== height) {
              canvas.width = width;
              canvas.height = height;
            }
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
          }

          // Moves current towards target; quick on the way up, slower on the way down.
          function ease(current, target, dt, up, down) {
            return current + (target - current) * (1 - Math.exp(-dt * (target > current ? up : down)));
          }

          // Speech keeps most bands well above the floor; this spreads them so pitch differences show.
          function spread(band) {
            return Math.pow(Math.max(0, (band - 0.3) / 0.7), 1.6);
          }

          function tick(now) {
            vz.raf = 0;
            if (vz.style === 'off') return;
            const dt = Math.min(0.05, (now - vz.last) / 1000);
            vz.last = now;
            sizeCanvas();

            if (vz.w > 0 && vz.h > 0) {
              const t = vz.target;
              vz.level = ease(vz.level, t.level || 0, dt, 30, 6);
              for (let i = 0; i < BANDS; i++) {
                vz.bands[i] = ease(vz.bands[i], spread(t.bands[i] || 0), dt, 35, 8);
                vz.peaks[i] = Math.max(vz.bands[i], vz.peaks[i] - dt * 0.35);
              }
              for (let i = 0; i < WAVE; i++) vz.wave[i] = ease(vz.wave[i], t.wave[i] || 0, dt, 40, 40);

              const css = getComputedStyle(document.documentElement);
              const look = {
                main: css.getPropertyValue('--focus').trim(),
                dim: css.getPropertyValue('--fg').trim(),
                glow: document.documentElement.style.colorScheme === 'dark' ? 14 : 0
              };
              // At rest the level never quite reaches zero, which gives the faint slow pulse.
              const rest = 0.05 + 0.03 * Math.sin(now / 900);
              const level = Math.max(vz.level, rest);

              ctx.clearRect(0, 0, vz.w, vz.h);
              ctx.globalAlpha = 1;
              ctx.lineCap = 'round';
              ctx.shadowColor = look.main;
              ctx.shadowBlur = look.glow;
              (styles[vz.style] || styles.orb)(vz.w, vz.h, level, dt, now, look);
              ctx.globalAlpha = 1;
              ctx.shadowBlur = 0;
            }

            // Keep going until any ring has faded, so none is left frozen on the canvas.
            if (now < vz.awakeUntil || vz.rings.length > 0) vz.raf = requestAnimationFrame(tick);
          }

          const styles = {
            orb(w, h, level, dt, now, look) {
              const cx = w / 2, cy = h / 2, base = h * 0.11;
              // A ring leaves the core each time the level jumps.
              if (level - vz.trend > 0.12 && vz.rings.length < 6) vz.rings.push({ r: base * (1 + level), a: 0.7 });
              vz.trend = ease(vz.trend, level, dt, 8, 8);
              vz.spin += dt * (0.3 + level * 2.2);

              ctx.lineWidth = 1.5;
              ctx.strokeStyle = look.main;
              for (const ring of vz.rings) {
                ring.r += dt * h * 0.7;
                ring.a -= dt * 0.9;
                ctx.globalAlpha = Math.max(0, ring.a);
                ctx.beginPath();
                ctx.arc(cx, cy, ring.r, 0, TAU);
                ctx.stroke();
              }
              vz.rings = vz.rings.filter(ring => ring.a > 0);

              // Two broken rings that turn in opposite directions, faster as the voice gets louder.
              ctx.lineWidth = 2;
              for (const [k, turn, alpha] of [[0.36, vz.spin, 0.75], [0.45, -vz.spin * 0.6, 0.4]]) {
                ctx.globalAlpha = alpha;
                for (let arc = 0; arc < 3; arc++) {
                  const from = turn + arc * TAU / 3;
                  ctx.beginPath();
                  ctx.arc(cx, cy, h * k, from, from + TAU / 3 - 0.5);
                  ctx.stroke();
                }
              }

              ctx.fillStyle = look.main;
              const radius = base * (0.8 + level * 1.5);
              for (const [scale, alpha] of [[1.35, 0.18], [1.15, 0.4], [0.85, 1]]) {
                ctx.globalAlpha = alpha;
                ctx.beginPath();
                ctx.arc(cx, cy, radius * scale, 0, TAU);
                ctx.fill();
              }
            },

            ring(w, h, level, dt, now, look) {
              const cx = w / 2, cy = h / 2, inner = h * 0.2, reach = h * 0.26;
              ctx.strokeStyle = look.dim;
              ctx.globalAlpha = 0.35;
              ctx.lineWidth = 1;
              ctx.shadowBlur = 0;
              ctx.beginPath();
              ctx.arc(cx, cy, inner - 4, 0, TAU);
              ctx.stroke();

              ctx.strokeStyle = look.main;
              ctx.globalAlpha = 1;
              ctx.lineWidth = 3;
              ctx.shadowBlur = look.glow;
              ctx.beginPath();
              for (let i = 0; i < BANDS; i++) {
                const length = 3 + Math.max(vz.bands[i], level * 0.15) * reach;
                // Low bands at the top, high at the bottom, mirrored on the left.
                const angle = -Math.PI / 2 + (i + 0.5) / BANDS * Math.PI;
                for (const a of [angle, Math.PI - angle]) {
                  const x = Math.cos(a), y = Math.sin(a);
                  ctx.moveTo(cx + x * inner, cy + y * inner);
                  ctx.lineTo(cx + x * (inner + length), cy + y * (inner + length));
                }
              }
              ctx.stroke();
            },

            bars(w, h, level, dt, now, look) {
              const gap = 5, total = Math.min(w - 40, 520), bar = (total - gap * (BANDS - 1)) / BANDS;
              const left = (w - total) / 2, floor = h - 14, tall = h - 30;
              ctx.fillStyle = look.main;
              for (let i = 0; i < BANDS; i++) {
                const height = Math.max(2, Math.max(vz.bands[i], level * 0.08) * tall);
                ctx.globalAlpha = 0.9;
                ctx.fillRect(left + i * (bar + gap), floor - height, bar, height);
              }
              ctx.fillStyle = look.dim;
              ctx.globalAlpha = 0.5;
              ctx.shadowBlur = 0;
              for (let i = 0; i < BANDS; i++) {
                ctx.fillRect(left + i * (bar + gap), floor - Math.max(4, vz.peaks[i] * tall) - 4, bar, 2);
              }
            },

            wave(w, h, level, dt, now, look) {
              const pad = 16, mid = h / 2, reach = h * 0.4;
              const trace = sign => {
                ctx.beginPath();
                for (let i = 0; i < WAVE; i++) {
                  const x = pad + i / (WAVE - 1) * (w - pad * 2);
                  // A slow ripple keeps the line alive at rest.
                  const y = mid - sign * (vz.wave[i] * reach + Math.sin(i * 0.45 + now / 450) * 1.5);
                  if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
                }
                ctx.stroke();
              };
              ctx.lineJoin = 'round';
              ctx.strokeStyle = look.dim;
              ctx.globalAlpha = 0.25;
              ctx.lineWidth = 1.5;
              ctx.shadowBlur = 0;
              trace(-1);
              ctx.strokeStyle = look.main;
              ctx.globalAlpha = 1;
              ctx.lineWidth = 2;
              ctx.shadowBlur = look.glow;
              trace(1);
            },

            swarm(w, h, level, dt, now, look) {
              if (vz.dots.length === 0) {
                for (let i = 0; i < 140; i++) {
                  vz.dots.push({
                    angle: Math.random() * TAU,
                    orbit: 0.25 + Math.random() * 0.75,
                    speed: (0.25 + Math.random() * 0.9) * (Math.random() < 0.5 ? -1 : 1),
                    band: Math.floor(Math.random() * BANDS),
                    radius: 0.3
                  });
                }
              }
              const cx = w / 2, cy = h / 2, rx = Math.min(w * 0.42, h * 1.7), ry = h * 0.42;
              ctx.fillStyle = look.main;
              ctx.globalAlpha = 0.35 + 0.6 * Math.min(1, level * 1.4);
              ctx.beginPath();
              for (const dot of vz.dots) {
                const band = vz.bands[dot.band];
                dot.angle += dt * dot.speed * (0.4 + level * 1.8);
                // Loud syllables fling the dots out; in quiet they drift back towards the centre.
                dot.radius = ease(dot.radius, dot.orbit * (0.3 + level * 0.75) + band * 0.2, dt, 10, 3);
                const x = cx + Math.cos(dot.angle) * dot.radius * rx;
                const y = cy + Math.sin(dot.angle) * dot.radius * ry;
                const size = 1 + band * 2.2;
                ctx.moveTo(x + size, y);
                ctx.arc(x, y, size, 0, TAU);
              }
              ctx.fill();
            }
          };

          window.addEventListener('resize', wake);
          if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
            window.chrome.webview.addEventListener('message', e => pushAudio(e.data));
          }
        </script>
        </body>
        </html>
        """;

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

        var done = new TaskCompletionSource();
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnCompleted;
            done.TrySetResult();
        }
        core.NavigationCompleted += OnCompleted;
        // Injected rather than linked: the page's content policy allows no script sources.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadMermaid());
        core.NavigateToString(ShellHtml);
        await done.Task;

        core.NavigationStarting += OnNavigationStarting;
        _loaded = true;
        if (_theme is not null) SetTheme(_theme);
        if (_text is not null) SetText(_text);
        SetVisualizer(_visualizer);
    }

    private static string LoadMermaid()
    {
        using var stream = typeof(DocumentView).Assembly.GetManifestResourceStream("mermaid.min.js")
            ?? throw new InvalidOperationException("The mermaid script is missing from the app.");
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
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException
                                       or NullReferenceException)
        {
            // The browser process went away or the window is closing; this runs 30 times a second,
            // so there is nothing useful to log.
        }
    }

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
