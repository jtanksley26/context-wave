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

    // Shared by every Context Wave process in the user's session.
    private static readonly Mutex Mutex = new(false, @"Local\ContextWave.FileLog");

    /// <summary>Waits briefly for the other writers; a line is still written if one has hung.</summary>
    private static bool TakeMutex()
    {
        try
        {
            return Mutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            // A writer died holding it; this thread owns it now.
            return true;
        }
    }

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
                var file = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyy-MM-dd}.log");
                var line = System.Text.Encoding.UTF8.GetBytes($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
                // The app, every bridge and every reply hook append to the same file. Opening it
                // shared stops a second writer failing, and the mutex shared by every process stops two writers
                // appending at the same offset and overwriting each other's line.
                var owned = TakeMutex();
                try
                {
                    using var stream = new FileStream(
                        file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(line);
                }
                finally
                {
                    if (owned) Mutex.ReleaseMutex();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
