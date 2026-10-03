# Reading Claude's Replies — Design

Date: 2026-10-03
Status: Approved design, pending implementation plan

## Purpose

Read Claude's replies aloud automatically, without Claude having to call a tool. Today
Claude speaks only when it decides to call `speak`, which costs tokens and can be
forgotten.

## Decisions

| Topic | Decision |
|---|---|
| Mechanism | A Claude Code `Stop` hook runs the Bridge, which forwards the reply to the app |
| What is read | The final reply of each turn (`last_assistant_message`); not interim notes or subagent replies |
| Overlap | A user setting: Off, Switch to the newest, Queue, Finish the current one |
| Default | Off |
| Priority | A file, a `speak` stream or a diff walkthrough is never interrupted by a reply |
| App not running | The hook does nothing; it never launches the app |
| Scope | Every Claude Code session on the machine (terminal and the desktop app's Code tab) |

## Background: the hook

Claude Code runs configured commands at lifecycle events. On `Stop` (Claude has finished
responding) the command receives JSON on standard input that includes
`hook_event_name: "Stop"`, `session_id`, `cwd` and `last_assistant_message`, the complete
text of the reply. A hook that exits 0 and prints nothing does not affect Claude.
`SubagentStop` is a separate event and is not used. Hooks are configured under `hooks`
in `~/.claude/settings.json`:

```json
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "command": "<path>\\MdReader.Bridge.exe", "args": ["reply-hook"], "timeout": 10, "async": true } ] }
    ]
  }
}
```

With `args` present the command is executed directly rather than through a shell, so no
quoting of the Windows path is needed. `async` lets Claude carry on without waiting.

The exact input is confirmed during implementation by capturing one real hook message
before the rest is built on it.

## Behaviour

- **Settings > Claude's replies** lists the four modes with a tick on the current one.
  The choice applies at once and is saved.
  - **Off** — replies are ignored.
  - **Switch to the newest** — a new reply replaces whatever reply is being read.
  - **Queue** — a new reply is appended after the reply being read.
  - **Finish the current one** — a new reply is ignored while a reply is being read or
    paused.
- A reply is read only when the reader is free or is reading a reply. The reader is
  "free" when it is idle with nothing loaded, or idle having finished whatever it had.
  If it is playing or paused on a file, a `speak` stream or a diff walkthrough, the
  reply is skipped whatever the mode. A loaded diff also counts as busy, even when idle,
  so a reply never replaces a walkthrough's text.
- While a reply is the current document the window title is "Claude's reply - MD Reader".
- Replies use the normal speech rules (code blocks announced, bare links skipped) and
  the normal highlighting, visualiser and controls.
- The voice must be installed; if not, the reply is skipped.
- An empty or whitespace-only reply is skipped.
- A reply longer than 200,000 characters is skipped.

## Components

### MdReader.Core

- **ReplyMode** — the ids `off`, `switch`, `queue`, `finish` with display names, a
  default (`off`) and `Normalize(id)`, in the style of `VisualizerCatalog`.
- **Settings** — adds `Replies` (default `off`).
- **Protocol** — new op `speak_reply`, using the existing `Text` field. `PipeResult`
  needs nothing new; the response message says what happened.
- **ReaderSession**
  - A new source value, `"reply"`, alongside `""`, `"stream"` and a file path.
  - The session is given the reply mode through a `Func<string>` like the existing
    settings callbacks.
  - `SpeakReply(text)` returns one of: read, queued, or skipped with the reason
    (off, busy, already reading, voice not ready, empty, too long). It applies the
    rules under Behaviour: replace when the reader is free or the mode is `switch` and
    the source is `reply`; append when the mode is `queue` and the source is `reply`
    and it is playing or paused; otherwise skip.
  - `speak_reply` in `Handle` returns success with a short message in every case, so
    the hook never reports an error for a normal skip.
  - `speak`, `read_file`, `show_diff` and opening a file behave as before; they replace
    a reply being read like any other document. `speak` in append mode while the source
    is `reply` starts a new `stream` document rather than appending to the reply.

### MdReader.Bridge

- **ReplyHook** — `Run(TextReader input, Func<PipeRequest, Task<PipeResponse>> send)`:
  reads standard input, parses the JSON, and when `hook_event_name` is `Stop` and
  `last_assistant_message` is a non-empty string, sends `speak_reply`. It uses a direct
  `PipeClient.SendAsync` with a short connect timeout, not `AppLink`, so a closed app is
  never launched. Every failure (bad JSON, missing fields, no app, timeout, pipe error)
  is swallowed. It prints nothing and always returns 0.
- **Program** — `reply-hook` as a second command-line mode beside `setup`.
- **ClaudeSettings** (new, like `DesktopConfig`) — pure functions over the text of
  `~/.claude/settings.json`: `HasHook`, and `Apply(existingJson, bridgeExePath, remove)`
  which adds or removes exactly the md-reader `Stop` entry, identified by its command
  ending in `MdReader.Bridge.exe` with the argument `reply-hook`. Other settings and
  other hooks, including other `Stop` hooks, are preserved. Re-running setup updates the
  path rather than adding a second entry. Removing the last `Stop` entry removes the
  empty `Stop` array, and an empty `hooks` object.
- **SetupCommand** — after the existing two registrations, applies `ClaudeSettings` to
  `~/.claude/settings.json` with the same backup-first, report-what-changed behaviour.
  The file is created if it does not exist. `setup --remove` removes the hook.

### MdReader.App

- **MainWindow** — the "Claude's replies" submenu, built from `ReplyMode`, one item
  ticked; the session's reply-mode callback reads the setting; the title for a reply.

The reading queue, TTS, diff handling, themes, the visualiser and the MCP tools are not
changed.

## Error handling

| Situation | Behaviour |
|---|---|
| App not running | Hook exits 0 silently |
| Mode is Off | App answers "skipped"; hook exits 0 |
| Reader busy with a file, stream or walkthrough | Skipped |
| Hook input is not JSON, or lacks the fields | Hook exits 0 silently |
| Pipe error or timeout | Hook exits 0 silently |
| `settings.json` is not a JSON object, or `hooks` / `Stop` have an unexpected shape | Setup leaves the file unchanged and reports why; exit code 1 |
| `settings.json` missing | Setup creates it with only the hook |
| Unknown reply mode in settings | Treated as Off |

## Testing

- **Unit, session:** for each mode, a reply arriving when free, when a reply is playing,
  and when a reply is paused; skipped when a file, a stream or a diff is current; Off
  ignores; voice not ready, empty and too-long replies are skipped; `speak` and
  `read_file` after a reply; the `reply` source and status.
- **Unit, hook:** a real-shaped `Stop` message sends `speak_reply` with the text; other
  events, missing or empty message, invalid JSON and empty input send nothing; a
  failing sender does not throw; the return value is always 0.
- **Unit, settings file:** add to an empty file, to a file with unrelated settings, and
  beside an existing `Stop` hook; re-adding updates the path without duplicating;
  removing leaves other hooks and cleans up empty containers; malformed shapes throw
  `InvalidDataException`; `HasHook`.
- **Integration:** the hook against an in-process `PipeServer`; with no server it
  returns promptly.
- **Manual:** run setup, start a new Claude Code session, and confirm a reply is read in
  each mode and skipped during a file reading; `setup --remove` stops it.

## Amendments (2026-10-03, from implementation planning)

- The hook entry uses the shell form, `"<path with forward slashes>/MdReader.Bridge.exe" reply-hook`,
  with `timeout: 10` and `async: true`, instead of `command` plus `args`. The shell form
  is the long-standing one and works whether Git Bash or cmd runs it. An existing entry
  written either way is recognised when updating or removing.
- The real hook message is not captured before the build. Instead, a `Stop` event that
  lacks `last_assistant_message` is written to the log with the names of the fields it
  did have, so a wrong assumption is visible at the manual check.
- The Bridge reads standard input as UTF-8 explicitly.
- `ReplyHook.RunAsync` takes the sender as a parameter; `ReplyHook.SendTo(pipeName)`
  provides the real one (300 ms to connect, 5 s overall).
- A reply queued behind another is separated from it by a horizontal rule.
- The reader counts as free whenever the queue is idle and no diff is loaded.
- Setup skips the hook when `~/.claude` does not exist, rather than creating the folder.
  The hook step runs before the Claude desktop app step.

## Out of scope

Reading replies as they stream; interim messages between tool calls; subagent replies;
the ordinary Claude desktop chat (it has no hooks); choosing which sessions or projects
are read; a keyboard shortcut for the mode.
