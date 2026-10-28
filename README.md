# Context Wave

A Windows app that reads markdown aloud with a local neural voice and highlights the
sentence being spoken. Claude Code and the Claude desktop app can send it files or
text through MCP.

## Build

Requires the .NET SDK (9 or later) and the .NET 8 desktop runtime.

```bash
dotnet build MdReader.sln
```

The app and the MCP bridge are written to `out/`. Close Context Wave before rebuilding.
Once the bridge is registered, Claude Code and the Claude desktop app keep `out\MdReader.Bridge.exe` open, so close those sessions too before rebuilding.

To build and test while those are open, send the output somewhere else:

```bash
dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"
```

## Run

```bash
./out/MdReader.App.exe
```

The first time you pick a voice, the window offers to download it (Kokoro is about
320 MB, Piper about 67 MB). Voices, settings and logs live in `%LOCALAPPDATA%\MdReader`.

Clicking a sentence reads from there. Clicks in code blocks and diagrams, and clicks that finish
selecting text, do not, so text can be selected and copied.

## Appearance

Everything about how the window looks is under **View**:

- **Text size** (80% to 200%), **Font**, **Column width** and **Line spacing** for the reading
  text. Ctrl with plus, minus or 0, and Ctrl with the mouse wheel, also change the size. The Font
  menu lists Verdana, Georgia, Sitka Text, Atkinson Hyperlegible, OpenDyslexic and Lexend when
  they are installed.
- **Theme**: System (follows the Windows light/dark setting), Light, Dark, Dim, Sepia and High
  contrast.
- **Highlight colour**: the colour used for the sentence being read and for the diff lines
  being talked about.
- **Visualiser**: a panel above the text that moves with the voice (Orb, Ring spectrum, Bars,
  Waveform or Particle swarm). Off hides it and stops the animation.

Every choice applies at once and is remembered.

## Connect Claude

```bash
./out/MdReader.Bridge.exe setup
```

This registers the `md-reader` MCP server with Claude Code and the Claude desktop app
(restart the desktop app afterwards). Undo it with `setup --remove`.

Tools: `read_file(path)`, `speak(text, mode, focus)`, `show_diff(diff, title)`, `stop()`, `status()`.

For a code walkthrough, Claude calls `show_diff` with a unified diff and then `speak` with a
`focus` such as `src/Foo.cs:120-140`. The window shows the diff beside the explanation and
scrolls it to the lines being talked about. Clicking a diff line jumps to its explanation.

## Claude's replies and one tab per chat

`setup` also installs a Claude Code hook that passes each finished reply to Context Wave. It
applies to Claude Code sessions (the terminal and the desktop app's Code tab) started after
`setup` was run, and nothing is received unless Context Wave is already open.

Each Claude Code chat gets its own tab, named as Claude names the chat (or after its folder until
it has a name), and every reply is added to the end of its chat's tab, so a tab is that chat's
running record. Files you open get a tab of their own. Close a tab with its × or a middle click.

There is one voice, and it reads the tab in front. Choosing another tab stops reading where it
was; Play carries on from there. Two settings, each answering one question, decide the rest:

**Settings > Auto Read**: which chats' replies are read aloud by themselves.

- **Off** (the default): none. Replies are still added to their tabs, with a dot.
- **This tab**: the chat in front. Other chats' replies wait in their tabs, with a dot.
- **All chats**: every chat. A chat whose reply is read comes to the front.

**Settings > When something is playing**: what a new reply, or a chat's request to read something
(`speak`, `read_file`, `show_diff`), does when something is already being read or is paused.

- **Wait your turn** (the default): in its own tab it is read after what is playing. From another
  chat it waits in that chat's tab, with a dot, and the chats come forward one after another, in
  the order things arrived, once the tab in front has finished. Each is read from its first unread
  sentence, never from halfway.
- **Interrupt**: it is read at once. What was playing stops where it is, keeping its place, and
  the chat it came from comes forward.

A chat's request to read something is not a reply, so Auto Read does not silence it: with Wait it takes its turn even with Auto Read off, and is read from the
request itself, not from older replies left unread. With Auto Read off, focus only moves for such
requests.

The one exception is a diff walkthrough: a reply that arrives in a chat's tab while its walkthrough
is still to be read is dropped rather than mixed into it, and once it has been read the next reply
replaces it.

The earlier "Claude's replies" setting is translated the first time this version starts. A request
from an older bridge, which does not say which chat it is from, goes to the tab in front, or to a
"Claude" tab.

## Environment

Setting `MDREADER_HOME` changes where settings, logs and voices are stored (the
default is `%LOCALAPPDATA%\MdReader`). The tests use it to stay out of that folder.

## Test

```bash
dotnet test MdReader.sln --filter "Category!=Manual"
```

`--filter "Category=Manual"` runs a smoke test that downloads the Piper voice and
synthesizes a sentence.

---
*Last updated: 2026-10-20 by Drew Smith — replaced the "Claude's replies" modes with Auto Read (Off / This tab / All chats) and When something is playing (Wait / Interrupt); trimmed implementation detail*
