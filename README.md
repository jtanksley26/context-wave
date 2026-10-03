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

## Reading Claude's replies

`setup` also installs a Claude Code hook that passes each finished reply to Context Wave. Choose
what happens under **Settings > Claude's replies**:

- **Off** (the default): replies are not read.
- **Switch to the newest**: a new reply replaces the one being read.
- **Queue**: a new reply is read after the current one.
- **Finish the current one**: a new reply is ignored while one is being read.

A reply never interrupts a file, text Claude was asked to speak, or a diff walkthrough, and
nothing is read unless Context Wave is already open. The hook applies to Claude Code sessions
(the terminal and the desktop app's Code tab) started after `setup` was run.

## Environment

Setting `MDREADER_HOME` changes where settings, logs and voices are stored (the
default is `%LOCALAPPDATA%\MdReader`). The tests use it to stay out of that folder.

## Test

```bash
dotnet test MdReader.sln --filter "Category!=Manual"
```

`--filter "Category=Manual"` runs a smoke test that downloads the Piper voice and
synthesizes a sentence.
