namespace ContextWave.Core;

public static class AppPaths
{
    /// <summary>Environment variable that, when set and non-empty, replaces the default data folder.</summary>
    public const string HomeVariable = "MDREADER_HOME";

    public static string Root =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader");

    public static string Models => Path.Combine(Root, "models");
    public static string Logs => Path.Combine(Root, "logs");
    public static string WebViewData => Path.Combine(Root, "webview");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>What is being read right now, for other programs to show (<see cref="LiveStatusWriter"/>).</summary>
    public static string LiveFile => Path.Combine(Root, "live.json");
}

public static class FileLog
{
    /// <summary>How long a day's log is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

    private static readonly object Lock = new();

    /// <summary>Deletes the logs of days older than <see cref="KeepFor"/>; a new file starts every day.</summary>
    public static void DeleteOld()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Logs)) return;
            var cutoff = DateTime.Now - KeepFor;
            foreach (var log in Directory.GetFiles(AppPaths.Logs, "*.log"))
            {
                if (File.GetLastWriteTime(log) < cutoff) File.Delete(log);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old log that cannot be deleted now is deleted another day.
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(
                    Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
