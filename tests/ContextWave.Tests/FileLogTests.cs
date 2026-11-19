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
}
