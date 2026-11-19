using ContextWave.Core;

namespace ContextWave.Tests;

public class FileLogTests
{
    [Fact]
    public void Logs_older_than_the_keeping_period_are_deleted()
    {
        Directory.CreateDirectory(AppPaths.Logs);
        var old = Path.Combine(AppPaths.Logs, $"old-{Guid.NewGuid():N}.log");
        var recent = Path.Combine(AppPaths.Logs, $"recent-{Guid.NewGuid():N}.log");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTime(old, DateTime.Now - FileLog.KeepFor - TimeSpan.FromDays(1));
        File.SetLastWriteTime(recent, DateTime.Now - FileLog.KeepFor + TimeSpan.FromDays(1));

        FileLog.DeleteOld();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        File.Delete(recent);
    }

    [Fact]
    public void A_line_is_written_while_another_process_holds_the_log_open()
    {
        Directory.CreateDirectory(AppPaths.Logs);
        var file = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyy-MM-dd}.log");
        var marker = $"shared-{Guid.NewGuid():N}";

        // Another writer, opened the way the bridge and the reply hook open it.
        using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            FileLog.Write(marker);

        using var reader = new StreamReader(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.Contains(marker, reader.ReadToEnd());
    }
}
