using System.Diagnostics;

namespace ContextWave.Bridge;

public static class SetupCommand
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private sealed record CommandResult(int ExitCode, bool TimedOut, string Output)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    public static int Run(bool remove)
    {
        var exe = Environment.ProcessPath!;
        var exitCode = 0;

        if (!RunCmd("where claude").Succeeded)
        {
            Console.WriteLine("Claude Code: 'claude' command not found; skipped.");
        }
        else
        {
            // Also done before adding, so re-running setup updates the path instead of failing on a duplicate.
            var removed = RunCmd($"claude mcp remove --scope user {DesktopConfig.ServerName}");
            if (remove)
            {
                if (removed.TimedOut)
                {
                    Console.WriteLine("Claude Code: 'claude mcp remove' did not finish in time.");
                    exitCode = 1;
                }
                else
                {
                    // A non-zero exit here means there was nothing to remove.
                    Console.WriteLine(removed.ExitCode == 0
                        ? "Claude Code: removed md-reader."
                        : "Claude Code: md-reader was not registered.");
                }
            }
            else
            {
                var added = RunCmd($"claude mcp add --scope user {DesktopConfig.ServerName} -- \"{exe}\"");
                if (added.Succeeded)
                {
                    Console.WriteLine($"Claude Code: registered md-reader -> {exe}");
                }
                else
                {
                    Console.WriteLine(added.TimedOut
                        ? "Claude Code: 'claude mcp add' failed (timed out)."
                        : "Claude Code: 'claude mcp add' failed.");
                    if (added.Output.Length > 0) Console.WriteLine(added.Output);
                    exitCode = 1;
                }
            }
        }

        if (ApplyReplyHook(exe, remove) != 0) exitCode = 1;
        if (ApplyDesktopConfig(exe, remove) != 0) exitCode = 1;
        return exitCode;
    }

    /// <summary>Adds or removes the md-reader entry in the Claude desktop app's config.</summary>
    private static int ApplyDesktopConfig(string exe, bool remove)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("Claude desktop app: not installed; skipped.");
            return 0;
        }

        var file = Path.Combine(directory, "claude_desktop_config.json");
        try
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (remove && !DesktopConfig.HasEntry(existing))
            {
                // Nothing to remove: do not create, rewrite or back up anything.
                Console.WriteLine("Claude desktop app: md-reader was not registered.");
                return 0;
            }

            var updated = DesktopConfig.Apply(existing, exe, remove);
            if (updated == existing)
            {
                Console.WriteLine("Claude desktop app: md-reader is already registered.");
                return 0;
            }

            if (existing is not null)
                Console.WriteLine($"Claude desktop app: backup saved to {ConfigFile.Backup(file)}");
            ConfigFile.WriteAtomically(file, updated);
            Console.WriteLine(remove
                ? "Claude desktop app: removed md-reader. Restart the app to apply."
                : "Claude desktop app: registered md-reader. Restart the app to apply.");
            return 0;
        }
        catch (Exception ex) when (ConfigFile.IsExpectedFailure(ex))
        {
            Console.WriteLine($"Claude desktop app: config left unchanged ({ex.Message}).");
            return 1;
        }
    }

    /// <summary>Adds or removes the Stop hook that reads Claude's replies aloud.</summary>
    private static int ApplyReplyHook(string exe, bool remove)
    {
        // Claude Code keeps its settings in ~/.claude unless CLAUDE_CONFIG_DIR points elsewhere.
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("Claude Code replies hook: no .claude folder; skipped.");
            return 0;
        }

        var result = ClaudeSettingsFile.Update(directory, exe, remove);
        if (result.BackupPath is not null)
            Console.WriteLine($"Claude Code replies hook: backup saved to {result.BackupPath}");
        switch (result.Outcome)
        {
            case ClaudeSettingsFile.Outcome.NotInstalled:
                Console.WriteLine("Claude Code replies hook: was not installed.");
                return 0;
            case ClaudeSettingsFile.Outcome.AlreadyInstalled:
                Console.WriteLine("Claude Code replies hook: already installed.");
                return 0;
            case ClaudeSettingsFile.Outcome.Failed:
                Console.WriteLine($"Claude Code replies hook: settings left unchanged ({result.Error}).");
                return 1;
            default:
                Console.WriteLine(remove
                    ? "Claude Code replies hook: removed. Start a new Claude Code session to apply."
                    : "Claude Code replies hook: installed. Start a new Claude Code session to apply, " +
                      "then choose a mode under Settings > Auto Read in Context Wave.");
                return 0;
        }
    }

    /// <summary>
    /// How <see cref="RunCmd"/> starts cmd.exe. Setup may be run from a terminal open in any folder, such as
    /// a cloned repository, and cmd.exe looks in the current folder before PATH. A planted cmd.exe,
    /// where.bat or claude.bat there would run as the user, so none of the three is looked up there.
    /// </summary>
    public static ProcessStartInfo CommandStart(string commandLine)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c {commandLine}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        return start;
    }

    /// <summary>Runs a command line through cmd.exe (claude is a .cmd shim) and captures its output.</summary>
    private static CommandResult RunCmd(string commandLine)
    {
        using var process = Process.Start(CommandStart(commandLine))!;

        // Both streams are drained concurrently: reading them one after the other can deadlock
        // once the child fills the pipe that is not being read.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();

        var timedOut = !process.WaitForExit((int)CommandTimeout.TotalMilliseconds);
        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to kill; either way stop waiting for it.
            }
        }

        // The pipes close when the process (tree) ends; do not wait forever if a stray child keeps them open.
        var drained = Task.WaitAll([stderr, stdout], TimeSpan.FromSeconds(5));
        var output = drained ? string.Join(Environment.NewLine,
            new[] { stderr.Result.Trim(), stdout.Result.Trim() }.Where(s => s.Length > 0)) : "";
        return new CommandResult(timedOut ? -1 : process.ExitCode, timedOut, output);
    }
}
