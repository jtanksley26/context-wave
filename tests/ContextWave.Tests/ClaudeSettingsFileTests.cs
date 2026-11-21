using ContextWave.Bridge;

namespace ContextWave.Tests;

public sealed class ClaudeSettingsFileTests : IDisposable
{
    private const string Exe = @"C:\Apps\ContextWave.Bridge.exe";

    // A fresh folder per test, so nothing here can touch the user's real Claude folder.
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"ContextWave.Test.{Guid.NewGuid():N}")).FullName;

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private string[] Backups() => Directory.GetFiles(_directory, "settings.json.bak-*");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Running_setup_twice_writes_and_backs_up_only_once()
    {
        File.WriteAllText(SettingsPath, """{ "theme": "dark" }""");

        var first = ClaudeSettingsFile.Update(_directory, Exe, remove: false);
        var written = File.ReadAllText(SettingsPath);
        var second = ClaudeSettingsFile.Update(_directory, Exe, remove: false);

        Assert.Equal(ClaudeSettingsFile.Outcome.Changed, first.Outcome);
        Assert.Equal(ClaudeSettingsFile.Outcome.AlreadyInstalled, second.Outcome);
        Assert.Null(second.BackupPath);
        Assert.Equal(written, File.ReadAllText(SettingsPath));
        Assert.Equal([first.BackupPath!], Backups());
        Assert.True(ClaudeSettings.HasHook(written));
    }

    [Fact]
    public void A_missing_settings_file_is_created_without_a_backup()
    {
        var result = ClaudeSettingsFile.Update(_directory, Exe, remove: false);

        Assert.Equal(ClaudeSettingsFile.Outcome.Changed, result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.Empty(Backups());
        Assert.True(ClaudeSettings.HasHook(File.ReadAllText(SettingsPath)));
    }

    [Fact]
    public void Malformed_settings_are_left_untouched_and_reported()
    {
        const string broken = "{ not json";
        File.WriteAllText(SettingsPath, broken);

        var result = ClaudeSettingsFile.Update(_directory, Exe, remove: false);

        Assert.Equal(ClaudeSettingsFile.Outcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(broken, File.ReadAllText(SettingsPath));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Settings_that_are_not_an_object_are_left_untouched_and_reported()
    {
        File.WriteAllText(SettingsPath, "[1, 2]");

        var result = ClaudeSettingsFile.Update(_directory, Exe, remove: false);

        Assert.Equal(ClaudeSettingsFile.Outcome.Failed, result.Outcome);
        Assert.Equal("[1, 2]", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Remove_without_our_hook_creates_no_file_and_no_backup()
    {
        var result = ClaudeSettingsFile.Update(_directory, Exe, remove: true);

        Assert.Equal(ClaudeSettingsFile.Outcome.NotInstalled, result.Outcome);
        Assert.False(File.Exists(SettingsPath));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Remove_without_our_hook_leaves_an_existing_file_alone()
    {
        const string text = """{ "theme": "dark" }""";
        File.WriteAllText(SettingsPath, text);

        var result = ClaudeSettingsFile.Update(_directory, Exe, remove: true);

        Assert.Equal(ClaudeSettingsFile.Outcome.NotInstalled, result.Outcome);
        Assert.Equal(text, File.ReadAllText(SettingsPath));
        Assert.Empty(Backups());
    }

    [Fact]
    public void Remove_takes_our_hook_out_and_backs_up_the_old_file()
    {
        ClaudeSettingsFile.Update(_directory, Exe, remove: false);

        var result = ClaudeSettingsFile.Update(_directory, Exe, remove: true);

        Assert.Equal(ClaudeSettingsFile.Outcome.Changed, result.Outcome);
        Assert.False(ClaudeSettings.HasHook(File.ReadAllText(SettingsPath)));
        Assert.Equal([result.BackupPath!], Backups());
    }

    [Fact]
    public void A_successful_write_leaves_no_temporary_file()
    {
        File.WriteAllText(SettingsPath, "{}");
        ClaudeSettingsFile.Update(_directory, Exe, remove: false);

        Assert.DoesNotContain(Directory.GetFiles(_directory), f => f.EndsWith(".contextwave-tmp"));
    }
}
