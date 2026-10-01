namespace MdReader.Core;

public static class AppPaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader");

    public static string Models => Path.Combine(Root, "models");
    public static string Logs => Path.Combine(Root, "logs");
    public static string WebViewData => Path.Combine(Root, "webview");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
}

public static class FileLog
{
    private static readonly object Lock = new();

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
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
