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
was; Play carries on from there. Two settings decide what happens when a reply arrives:

**Settings > Claude's replies**: how the tab in front reads a new reply.

- **Off** (the default): replies are still added to their tabs (with a dot), but nothing is read
  aloud by itself.
- **Read the newest right away**: a new reply interrupts and is read at once.
- **Read after the current one**: a new reply is read when the current one finishes.

**Settings > Tab focus**: whether another chat's reply brings its tab forward.

- **Stay on this tab** (the default): the reply is added to its chat's tab, which gets a dot.
- **Move to the next chat when done**: once the tab in front has nothing left to read (including
  replies that arrived in it meanwhile), the chats with unread replies come forward one after
  another, in the order their replies arrived. Each is read from its first unread reply to the end,
  never from halfway.
- **Jump to the newest**: the chat that replied comes forward at once and its new reply is read,
  unless the tab in front is reading something other than replies (see below).

With replies off, focus never moves by itself either. A reply never interrupts a file, text Claude
was asked to speak, or a diff walkthrough, whether in its own tab or in the tab in front: it is
added without being read (with a dot when its tab is not in front). The one exception is a
walkthrough in the reply's own tab: while the walkthrough is still to be read the reply is
dropped, and once it has been read the next reply replaces it.

The reply hook sends the chat's session id and the chat's process (the one that started its tool
bridge); each chat's tool calls send the same process. That is how a tool call from a chat (`read_file`, `speak`, `show_diff`) lands
in that chat's tab and brings it forward. A request that carries neither, from an older bridge,
goes to the tab in front, or to a "Claude" tab.

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
*Last updated: 2026-10-08 by Drew Smith — merged the replies section into the tab section; noted that code blocks and diagrams are not click-to-read*
