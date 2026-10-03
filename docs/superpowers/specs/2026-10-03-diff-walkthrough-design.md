# Diff Walkthrough — Design

Date: 2026-10-03
Status: Approved design, pending implementation plan

## Purpose

Let Claude walk the user through a pull request or a set of code changes in MD Reader.
The window shows the diff in one pane and Claude's explanation in the other. As each
sentence of the explanation is spoken, the diff scrolls to the lines it is about and
highlights them.

## Decisions

| Topic | Decision |
|---|---|
| Sync | Follow along: each chunk of explanation is tied to a file or line range in the diff |
| Diff source | Claude sends unified diff text; the reader does not run git or call GitHub |
| Wiring | New `show_diff` tool, plus an optional `focus` parameter on `speak` |
| Layout | Diff on the left, explanation on the right, draggable divider, one WebView |
| Diff style | Unified, one column |

## How a walkthrough runs

1. The user asks Claude to review a PR or go over some code.
2. Claude obtains the unified diff and calls `show_diff`. The reader stops any current
   reading, clears the document, and opens the diff pane.
3. Claude calls `speak` once per point, each with a `focus`. Reading starts on the first
   call and continues as later calls arrive, exactly as streaming works today.
4. When a sentence starts, the diff pane scrolls to that sentence's lines and highlights
   them.
5. The diff stays loaded until `stop`, another `show_diff`, `read_file`, or a file opened
   from the window. `speak` with `mode: replace` clears only the document and the
   sentence-to-diff links; the diff remains.

## MCP tools

| Tool | Parameters | Behaviour |
|---|---|---|
| `show_diff` (new) | `diff` (unified diff text), `title` (optional) | Stops reading, clears the document, parses and displays the diff. Does not require the voice to be installed. Returns the file count. |
| `speak` | adds `focus` (optional) | Every sentence in the chunk is linked to the resolved location. Without `focus` the sentences are unlinked. |
| `status` | none | Also returns `diffFiles`: the number of files in the loaded diff, 0 when none. |

`read_file` and `stop` keep their parameters and additionally unload the diff.

The `show_diff` and `speak` descriptions tell Claude to use them for PR reviews and code
walkthroughs: call `show_diff` once, then `speak` with a `focus` for each point.

### Focus syntax

`path`, `path:line`, or `path:start-end`.

- Line numbers are those of the new version of the file.
- `path` alone links to the whole file; the pane scrolls to its header and highlights the
  header.
- Path matching ignores a leading `a/` or `b/`, treats `\` as `/`, and accepts either an
  exact match or a unique match on the end of the path (so `Foo.cs` matches
  `src/App/Foo.cs` when no other file ends in `/Foo.cs`). Matching is case-insensitive.
- A range resolves to the rendered lines from the first to the last new-file line that
  falls inside it. Removed lines that sit between those are included.
- A range that touches no line shown in the diff for that file resolves to the file.
- A deleted file can only be focused by path.

### When focus does not resolve

The text is still queued and read. The tool result says so, for example
`Queued 3 sentences. Focus 'Foo.cs:900-910' was not found in the diff.` The same applies
when `focus` is given and no diff is loaded. An ambiguous suffix match counts as not
found and the message lists the candidates.

## Pipe protocol

- New op `show_diff`.
- `PipeRequest` gains `Diff`, `Title`, `Focus`.
- `PipeResult` gains `DiffFiles`.

## Components

### MdReader.Core

- **DiffDocument** (new) — no UI dependencies.
  - `static DiffDocument Parse(string unifiedDiff)`; throws `ReaderException` when the
    text contains no file with at least one hunk or binary marker.
  - Model: `DiffFile { Index, OldPath, NewPath, DisplayPath, Kind (Modified, Added,
    Deleted, Renamed, Binary), Lines }` and
    `DiffLine { Index, Kind (Context, Added, Removed, HunkHeader), OldNumber?, NewNumber?, Text }`.
    `Index` on a line is its position within the whole document, so it is a stable
    element id.
  - `string Html` — one `<section data-file="{i}">` per file with a header, then one
    `<div class="dl {kind}" data-line="{Index}">` per line holding old number, new
    number, marker and escaped text.
  - `DiffAnchor? Resolve(string focus)` where
    `DiffAnchor { FileIndex, FirstLine?, LastLine?, Label }`. `FirstLine`/`LastLine` are
    line `Index` values, null for a whole-file anchor. `Label` is the display form,
    e.g. `Foo.cs:120-140`.
  - Parsing handles: multiple files; `new file`, `deleted file`, `rename from/to`;
    `Binary files ... differ`; `\ No newline at end of file`; hunk headers with and
    without counts; CRLF input. `diff --git` headers and plain `---`/`+++` headers are
    both accepted.
- **ReaderSession**
  - Holds `DiffDocument? _diff` and `Dictionary<int, DiffAnchor> _anchors` keyed by
    sentence id.
  - `ShowDiff(diff, title)`: validates size (limit 2 MB of UTF-8), parses, then stops
    the queue, resets the document, sets `Source = "stream"`, stores the diff, clears
    anchors, raises `DocumentReplaced("")` and `DiffReplaced(html, title)`. Parsing
    happens first so a bad diff leaves the current reading untouched.
  - `Speak(text, mode, focus)`: resolves `focus`, appends as today, records the anchor
    for each new sentence id, and prefixes the appended HTML with
    `<div class="focus-label">{Label}</div>` when the anchor resolved.
  - `AnchorFor(int sentenceId)` returns the anchor or null.
  - `SentenceForDiffLine(int lineIndex)` returns the lowest sentence id whose anchor
    covers that line, or null. A whole-file anchor covers every line of the file, but a
    line-range anchor is preferred over a whole-file one when both cover the line.
  - `Stop`, `OpenFile` and `read_file` clear the diff and anchors and raise
    `DiffReplaced("", null)`. `speak` in `replace` mode clears anchors only.
  - New event `DiffReplaced(string html, string? title)`.
- **Protocol** — fields listed above.

`ReadingQueue`, `MarkdownDocument`, `SpeechRules` and the TTS engine do not change.

### MdReader.App

- **DocumentView**
  - The shell page gains a `#diff` pane, a divider, and wraps the existing content in a
    `#text` pane. With no diff the body has no `split` class and renders exactly as
    today (single centred column, window scrolling). With a diff, the body is a
    two-column flex layout; each pane scrolls on its own.
  - `SetDiff(html, title)`, `FocusDiff(fileIndex, firstLine, lastLine)`, `ClearDiffFocus()`.
  - Sentence highlight scrolls within the text pane when split.
  - Clicking a `.dl` element or file header posts `{"diffLine": n}` or
    `{"diffFile": i}`; sentence clicks keep posting the bare id. New event
    `DiffLineClicked(int lineIndex)`; a file-header click reports the file's first line.
  - The divider is dragged in page script and sets the diff pane width as a percentage,
    clamped to 25–75%. The width is not persisted.
  - Diff colours are defined for light and dark alongside the existing variables.
- **MainWindow**
  - `DiffReplaced` → `SetDiff`, update title (`"{title} - MD Reader"` when a title is
    given), and widen the window once to 1300 px if it is narrower, clamped to the work
    area. It does not shrink back.
  - `SentenceStarted` → look up `AnchorFor(id)`; call `FocusDiff` or `ClearDiffFocus`.
  - `DiffLineClicked` → `SentenceForDiffLine`; if found and the voice is ready,
    `JumpTo`.

### MdReader.Bridge

- **ReaderTools** — `show_diff` tool; `focus` parameter on `speak`; `status` text gains a
  `diff: N files` line when a diff is loaded.

## Error handling

| Situation | Behaviour |
|---|---|
| `show_diff` with empty text | Tool error; current reading untouched |
| Diff larger than 2 MB | Tool error; not loaded |
| Text with no recognisable file or hunk | Tool error "not a unified diff"; current reading untouched |
| Binary file in the diff | Shown as a header with "Binary file"; focusable by path only |
| `focus` not found, ambiguous, or no diff loaded | Text is read; result message reports it |
| Click on a diff line no sentence covers | Ignored |

All diff text is HTML-escaped. The page's content security policy is unchanged.

## Testing

- **Unit, DiffDocument:** single and multiple files; added, deleted, renamed and binary
  files; "no newline" marker; old/new line numbering across several hunks; CRLF input;
  HTML escaping of `<`, `&` and quotes; rejects non-diff text.
- **Unit, focus resolution:** path only; single line; range; range containing removed
  lines; suffix match; ambiguous suffix; `a/`/`b/` prefix and backslashes; range outside
  the diff falls back to the file; unknown path.
- **Unit, ReaderSession:** `show_diff` clears the document and stops the queue; a bad
  diff changes nothing; anchors recorded per sentence; label prefixed to the HTML;
  unresolved focus still queues and reports; `replace` keeps the diff and drops anchors;
  `stop` and `OpenFile` unload the diff; `SentenceForDiffLine` picks the lowest covering
  sentence and prefers a range anchor; `status` reports the file count.
- **Integration:** pipe round trip carrying `Diff`, `Title`, `Focus` and `DiffFiles`;
  Bridge `show_diff` and `speak` with `focus` against the stub pipe server.
- **Manual:** split layout in light and dark, divider drag, follow-along scrolling and
  highlight, click-to-jump from the diff, return to the single-column layout after
  `stop` or opening a file.

## Amendments (2026-10-03, from implementation planning)

- `DiffDocument.Resolve` is `DiffAnchor? Resolve(string focus, out string? problem)`. The
  problem text is what the `speak` result reports when a focus does not resolve.
- A resolved range also takes in removed lines that sit directly before its first line,
  so a changed line is highlighted as its old and new text together.
- Diff clicks are reported as a file index plus an optional line index:
  `DocumentView.DiffClicked(int fileIndex, int? lineIndex)` and
  `ReaderSession.SentenceForDiff(int fileIndex, int? lineIndex)` replace
  `DiffLineClicked` and `SentenceForDiffLine`. The page posts `diff:{file}:{line}`, with
  the line empty for a file header. A header click jumps to the first sentence linked
  anywhere in that file, which also works for files with no lines (binary, pure rename).
- The diff pane is refocused only when the spoken sentence's anchor differs from the
  previous sentence's, so it does not undo manual scrolling within a chunk.
- `ReaderSession.DiffTitle` exposes the title for the window caption.
- A file with no hunks is kept when it came from a `diff --git` header (pure rename,
  mode change, empty new file) and shown as a header only.
- The App and Bridge output folder can be overridden with the `MdReaderOut` build
  property, so the code can be built and tested while a Claude session holds the bridge
  in `out/` open.

## Out of scope

Syntax colouring; word-level change marks within a line; two-column diffs; collapsing
files; opening `.diff` or `.patch` files from the File menu; the reader running git or
calling GitHub; persisting the divider position.
