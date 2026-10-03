Context Wave
============

Reads markdown aloud with a local voice and highlights the sentence being spoken.
Claude Code and the Claude desktop app can send it files, text and diffs.

Install
-------
1. Extract this whole zip to any folder.
2. Double-click Install.cmd.

It installs for your user only and needs no administrator rights. It copies the app to
%LOCALAPPDATA%\Programs\MdReader, adds "Context Wave" to the Start Menu, and installs the
bundled voice so nothing has to be downloaded. It then asks whether to connect Context Wave
to Claude on this PC.

Nothing else needs to be installed: the .NET runtime is included. The app does use the
Microsoft Edge WebView2 Runtime, which is part of Windows 11 and most Windows 10 PCs;
the installer tells you if it is missing.

If Install.cmd is blocked
-------------------------
Some work PCs do not allow scripts. In that case copy the "app" folder anywhere you like
and run MdReader.App.exe from it. To use the bundled voice, copy the folder inside
"voices" to %LOCALAPPDATA%\MdReader\models. To connect Claude, run:

    bridge\MdReader.Bridge.exe setup

The programs are not code-signed, so Windows may show a "Windows protected your PC"
message the first time. Choose "More info", then "Run anyway", if your PC allows it.

Connect Claude later, or disconnect
-----------------------------------
    "%LOCALAPPDATA%\Programs\MdReader\bridge\MdReader.Bridge.exe" setup
    "%LOCALAPPDATA%\Programs\MdReader\bridge\MdReader.Bridge.exe" setup --remove

Restart Claude afterwards. Reading Claude's replies aloud is off until you choose a mode
under Settings > Claude's replies in Context Wave.

Uninstall
---------
Run Uninstall.cmd in %LOCALAPPDATA%\Programs\MdReader. It removes the Claude
registration, the shortcut and the app, and asks before deleting your voices and
settings.

Requirements
------------
Windows 10 or 11, 64-bit.
