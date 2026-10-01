# MD Reader — Design

Date: 2026-10-01
Status: Approved design, pending implementation plan

## Purpose

A Windows desktop app that displays a markdown document and reads it aloud with a
local neural voice, highlighting the sentence being spoken. The user can open files
directly. Claude Code and the Claude desktop app can send it a file path or chunks of
markdown text through MCP, and the app reads them.

## Decisions

| Topic | Decision |
|---|---|
| Clients | Claude Code and the Claude desktop app, both via one MCP server |
| Voice | Local neural TTS through `sherpa-onnx`; Kokoro default, Piper as lighter option |
| Interface | Reader window: rendered markdown, sentence highlight, playback bar |
| Streaming | Claude calls `speak` repeatedly; chunks append to the current document and queue |
| Stack | .NET 8, WPF, WebView2, Markdig, NAudio, official C# MCP SDK |
| Platform | Windows 10/11 x64 only |

## Solution layout

```
MdReader.sln
  src/MdReader.Core/      class library, no UI dependencies
  src/MdReader.App/       WPF application (single instance)
  src/MdReader.Bridge/    console exe, stdio MCP server
  tests/MdReader.Tests/   xUnit tests for Core
```

### MdReader.Core

Has no reference to WPF, WebView2, or audio devices.

- **MarkdownDocument** — parses markdown with Markdig and produces two outputs from
  the same parse:
  - an ordered list of `Sentence { Id, SpokenText, PauseAfterMs }`
  - HTML in which each sentence is wrapped in `<span data-sid="{Id}">`
  
  Supports `Append(markdown)`, which parses the new chunk, assigns ids continuing from
  the last one, and returns only the new sentences and the new HTML fragment.
- **SpeechRules** — converts markdown elements to spoken text (see Speech rules).
- **ITtsEngine** — `Task<AudioClip> SynthesizeAsync(string text, VoiceSettings v, CancellationToken ct)`.
  `AudioClip` is mono float PCM plus a sample rate.
- **SherpaTtsEngine** — `ITtsEngine` implementation over the `sherpa-onnx` .NET package.
- **ReadingQueue** — the playback state machine. Holds the sentence list and a current
  index; states are `Idle`, `Playing`, `Paused`. Runs a background worker that keeps
  up to 3 sentences synthesized ahead of the current one. Raises `SentenceStarted(id)`,
  `StateChanged`, and `Finished`. Operations: `Load`, `Append`, `Play`, `Pause`,
  `Next`, `Previous`, `JumpTo(id)`, `Stop`.
- **IAudioOutput** — `Task PlayAsync(AudioClip, CancellationToken)`, `Pause()`, `Resume()`.
  The real implementation lives in the App project so Core stays device-free.
- **ModelStore** — locates, downloads, and verifies voice models under
  `%LOCALAPPDATA%\MdReader\models`.
- **Settings** — JSON file at `%LOCALAPPDATA%\MdReader\settings.json`: voice, speed,
  whether to announce code blocks.

### MdReader.App

- **MainWindow** — WebView2 document view plus a playback bar: previous, play/pause,
  next, speed, voice selector, elapsed position. Open-file menu item and drag-and-drop.
- **DocumentView** — loads the HTML from Core into WebView2, appends fragments,
  highlights and scrolls to a sentence on `SentenceStarted`, and reports a clicked
  sentence id back so the queue can `JumpTo` it.
- **NAudioOutput** — `IAudioOutput` over NAudio `WasapiOut`.
- **PipeServer** — listens on the named pipe, dispatches requests to the queue on the
  UI thread, returns responses.
- **Single instance** — a named mutex; a second launch brings the existing window
  forward and exits.

### MdReader.Bridge

A console executable that Claude launches as a stdio MCP server. It contains no
reading logic. For each tool call it connects to the pipe, sends one request, and
returns the response as the tool result. If the pipe does not exist it starts
`MdReader.App.exe` (located next to itself) and retries the connection for up to
10 seconds.

## MCP tools

| Tool | Parameters | Behaviour |
|---|---|---|
| `read_file` | `path` (absolute) | Replaces the current document with the file and starts reading. Accepts `.md`, `.markdown`, `.txt`. |
| `speak` | `text` (markdown), `mode` = `append` \| `replace`, default `append` | `append` adds the chunk to the end of the current document and queue; if idle, reading starts at the new chunk. `replace` clears the document first. |
| `stop` | none | Stops playback and clears the document and queue. |
| `status` | none | Returns `state`, `source` (file path or `"stream"`), `currentSentence`, `totalSentences`. |

All tools return a short text result. Failures return an MCP tool error with a
plain-language message.

## Pipe protocol

- Pipe name: `MdReader.{user SID}`; ACL grants access to the current user only.
- One JSON request and one JSON response per connection, each a single UTF-8 line.
- Request: `{ "op": "read_file" | "speak" | "stop" | "status", ...params }`
- Response: `{ "ok": true, "result": {...} }` or `{ "ok": false, "error": "message" }`

## Data flow

1. Markdown arrives from a file open, a drop, `read_file`, or `speak`.
2. `MarkdownDocument` produces sentences and HTML (or an appended fragment).
3. The App pushes the HTML into WebView2; `ReadingQueue` receives the sentences.
4. The queue worker synthesizes ahead; each finished clip is cached against its sentence id.
5. The queue plays clips in order through `IAudioOutput`. At the start of each one it
   raises `SentenceStarted`, and the view highlights and scrolls to that span.
6. After each clip the queue waits `PauseAfterMs`, then advances. At the end it raises
   `Finished` and returns to `Idle`, keeping the document on screen.

Jumping, skipping, or changing voice or speed cancels in-flight synthesis and discards
cached clips that are no longer valid. Speed and voice changes take effect from the
next sentence.

## Speech rules

| Element | Spoken as |
|---|---|
| Heading | Its text, followed by a 600 ms pause |
| Paragraph | Split into sentences; 250 ms pause after the last one |
| List item | Its text as one or more sentences; 200 ms pause after |
| Link | Link text only |
| Bare URL | Skipped |
| Inline code | Its text |
| Code block | Skipped; says "code block" when the announce setting is on (default on) |
| Table | Each row as one sentence, cells separated by commas |
| Image | Alt text, if any |
| Block quote | Its text, read normally |
| Emphasis, strikethrough markers | Removed; text read normally |
| Emoji, HTML tags, horizontal rules | Skipped |

Sentence splitting breaks on `.`, `!`, `?` followed by whitespace and a capital letter
or end of block, and does not break after a fixed list of common abbreviations
(e.g., i.e., etc., vs., Mr., Mrs., Dr.) or inside numbers.

## Voice

- Engine: `sherpa-onnx` .NET package, CPU inference.
- Models: Kokoro (default) and one Piper English voice. Each is downloaded on first
  selection from the sherpa-onnx model releases and verified by SHA-256.
- Speed range 0.5x–2.0x in 0.1 steps, passed to the model as its speed parameter.
- The voice selector lists the speakers available in installed models.

## Error handling

| Situation | Behaviour |
|---|---|
| `read_file` path missing, relative, or wrong extension | Tool error with the reason; current reading is untouched |
| File larger than 5 MB | Tool error; not loaded |
| App not running | Bridge launches it, retries the pipe for 10 s, then returns a tool error |
| Voice model not downloaded | Window shows a download prompt with progress; tools return "voice not ready" until done |
| Model download or hash check fails | Window shows the error with a Retry button; partial file is deleted |
| Synthesis fails for one sentence | Sentence is skipped, error is logged, reading continues |
| No audio output device | Playback pauses; status bar shows the problem; resumes on Play |
| WebView2 runtime absent | Startup dialog with the download link, then exit |

Logs go to `%LOCALAPPDATA%\MdReader\logs`, one file per day.

## Testing

- **Unit (Core):** speech rules for every row of the table above; sentence splitting
  including abbreviations and numbers; `Append` id continuity; `ReadingQueue` state
  transitions, look-ahead, jump, and cancellation, driven by a fake `ITtsEngine` and a
  fake `IAudioOutput`.
- **Integration:** pipe request/response round trip against an in-process `PipeServer`;
  Bridge tool calls against a stub pipe server.
- **Manual:** real voice playback, highlight tracking, and end-to-end calls from Claude
  Code and the Claude desktop app.

## Registration with Claude

A `setup` command on the Bridge (`MdReader.Bridge.exe setup`) that:

- runs `claude mcp add --scope user md-reader <path to Bridge exe>` when the `claude`
  CLI is found, and
- adds an `md-reader` entry to the Claude desktop app's `claude_desktop_config.json`,
  preserving existing entries and writing a backup first.

It prints what it changed. `setup --remove` reverses both.

## Out of scope for version 1

Cloud voices; automatically reading every Claude reply; word-level highlighting;
exporting audio to a file; tray icon and global hotkeys; non-English voices;
an installer package (the app runs from its build output folder).

## Build order

1. Core parsing and speech rules, with tests.
2. Reading queue with fakes, with tests.
3. App window: open a file, render, read with highlighting using the real voice.
4. Playback controls, speed, voice selection, click-to-jump, model download.
5. Pipe server and Bridge with the four MCP tools.
6. `setup` registration command.

## Amendments (2026-10-01, from implementation planning)

- `PipeServer` lives in `MdReader.Core` rather than the App, so the pipe round trip
  can be tested without WPF. The App supplies the request handler.
- The pipe has a fifth, internal operation, `activate`, used when the app is launched
  a second time to bring the existing window forward. It is not exposed as an MCP tool.
- `ReadingQueue.Load` and `Append` take an `autoPlay` flag. A file opened from the
  window before the voice is downloaded is displayed but not played.
- Images are shown as alt text only; the document page does not load remote images.
- The playback bar shows sentence position ("12 / 80") instead of elapsed time.
- Kokoro's download is about 320 MB and Piper's about 67 MB. The Piper voice is
  `en_US-lessac-medium`.
- The App and the Bridge build into a shared `out/` folder at the repository root.
