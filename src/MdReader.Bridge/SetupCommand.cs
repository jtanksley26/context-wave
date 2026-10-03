using System.Diagnostics;
using System.Text.Json;

namespace MdReader.Bridge;

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

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (!Directory.Exists(directory))
        {
            Console.WriteLine("Claude desktop app: not installed; skipped.");
            return exitCode;
        }

        var file = Path.Combine(directory, "claude_desktop_config.json");
        try
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (remove && !DesktopConfig.HasEntry(existing))
            {
                // Nothing to remove: do not create, rewrite or back up anything.
                Console.WriteLine("Claude desktop app: md-reader was not registered.");
                return exitCode;
            }

            var updated = DesktopConfig.Apply(existing, exe, remove);
            if (updated == existing)
            {
                Console.WriteLine("Claude desktop app: md-reader is already registered.");
                return exitCode;
            }

            if (existing is not null)
            {
                var backup = $"{file}.bak-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(file, backup);
                Console.WriteLine($"Claude desktop app: backup saved to {backup}");
            }
            File.WriteAllText(file, updated);
            Console.WriteLine(remove
                ? "Claude desktop app: removed md-reader. Restart the app to apply."
                : "Claude desktop app: registered md-reader. Restart the app to apply.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Claude desktop app: config left unchanged ({ex.Message}).");
            exitCode = 1;
        }
        return exitCode;
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

        var file = Path.Combine(directory, "settings.json");
        try
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (remove && !ClaudeSettings.HasHook(existing))
            {
                Console.WriteLine("Claude Code replies hook: was not installed.");
                return 0;
            }

            var updated = ClaudeSettings.Apply(existing, exe, remove);
            if (!string.IsNullOrWhiteSpace(existing)
                && System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(existing), System.Text.Json.Nodes.JsonNode.Parse(updated)))
            {
                Console.WriteLine("Claude Code replies hook: already installed.");
                return 0;
            }

            if (existing is not null)
            {
                var backup = $"{file}.bak-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(file, backup);
                Console.WriteLine($"Claude Code replies hook: backup saved to {backup}");
            }
            // Write beside the file and swap it in, so a failure cannot leave the settings half-written.
            var temporary = file + ".mdreader-tmp";
            File.WriteAllText(temporary, updated);
            File.Move(temporary, file, overwrite: true);
            Console.WriteLine(remove
                ? "Claude Code replies hook: removed. Start a new Claude Code session to apply."
                : "Claude Code replies hook: installed. Start a new Claude Code session to apply, " +
                  "then choose a mode under Settings > Claude's replies in MD Reader.");
            return 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Claude Code replies hook: settings left unchanged ({ex.Message}).");
            return 1;
        }
    }

    /// <summary>Runs a command line through cmd.exe (claude is a .cmd shim) and captures its output.</summary>
    private static CommandResult RunCmd(string commandLine)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commandLine}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

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
