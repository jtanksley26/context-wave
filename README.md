# MD Reader

A Windows app that reads markdown aloud with a local neural voice and highlights the
sentence being spoken. Claude Code and the Claude desktop app can send it files or
text through MCP.

## Build

Requires the .NET SDK (9 or later) and the .NET 8 desktop runtime.

```bash
dotnet build MdReader.sln
```

The app and the MCP bridge are written to `out/`. Close MD Reader before rebuilding.
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

**Settings > Theme** offers System (follows the Windows light/dark setting), Light, Dark, Dim,
Sepia and High contrast. **Settings > Highlight colour** sets the colour used for the sentence
being read and for the diff lines being talked about. Both apply at once and are remembered.

**Settings > Visualiser** shows a panel above the text that moves with the voice: Orb, Ring
spectrum, Bars, Waveform or Particle swarm. Off hides it and stops the animation.

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

## Environment

Setting `MDREADER_HOME` changes where settings, logs and voices are stored (the
default is `%LOCALAPPDATA%\MdReader`). The tests use it to stay out of that folder.

## Test

```bash
dotnet test MdReader.sln --filter "Category!=Manual"
```

`--filter "Category=Manual"` runs a smoke test that downloads the Piper voice and
synthesizes a sentence.
