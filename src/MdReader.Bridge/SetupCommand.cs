using System.Diagnostics;
using System.Text.Json;

namespace MdReader.Bridge;

public static class SetupCommand
{
    public static int Run(bool remove)
    {
        var exe = Environment.ProcessPath!;
        var exitCode = 0;

        // Remove first so re-running setup updates the path instead of failing on a duplicate.
        var removed = RunClaude($"mcp remove --scope user {DesktopConfig.ServerName}");
        if (removed is null)
        {
            Console.WriteLine("Claude Code: 'claude' command not found; skipped.");
        }
        else if (remove)
        {
            Console.WriteLine("Claude Code: removed md-reader.");
        }
        else if (RunClaude($"mcp add --scope user {DesktopConfig.ServerName} -- \"{exe}\"") == 0)
        {
            Console.WriteLine($"Claude Code: registered md-reader -> {exe}");
        }
        else
        {
            Console.WriteLine("Claude Code: 'claude mcp add' failed.");
            exitCode = 1;
        }

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
            var updated = DesktopConfig.Apply(existing, exe, remove);
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
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            Console.WriteLine($"Claude desktop app: config left unchanged ({ex.Message}).");
            exitCode = 1;
        }
        return exitCode;
    }

    /// <returns>The exit code, or null when the claude command does not exist.</returns>
    private static int? RunClaude(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c claude {arguments}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 9009 ? null : process.ExitCode;
    }
}
