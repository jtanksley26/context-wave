using ContextWave.Core;

namespace ContextWave.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("contextwave-settings-").FullName;
    private string File_ => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var settings = Settings.Load(File_);
        Assert.Equal("kokoro-en-v0_19", settings.ModelId);
        Assert.Equal(1, settings.SpeakerId);
        Assert.Equal(1.0f, settings.Speed);
        Assert.True(settings.AnnounceCodeBlocks);
    }

    [Fact]
    public void Saved_values_round_trip()
    {
        new Settings { ModelId = "x", SpeakerId = 4, Speed = 1.5f, AnnounceCodeBlocks = false }.Save(File_);
        var loaded = Settings.Load(File_);
        Assert.Equal(("x", 4, 1.5f, false), (loaded.ModelId, loaded.SpeakerId, loaded.Speed, loaded.AnnounceCodeBlocks));
    }

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ not json");
        Assert.Equal(1.0f, Settings.Load(File_).Speed);
    }

    [Fact]
    public void Null_model_id_and_negative_speaker_fall_back_to_safe_values()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{\"ModelId\":null,\"SpeakerId\":-3}");
        var settings = Settings.Load(File_);
        Assert.Equal("kokoro-en-v0_19", settings.ModelId);
        Assert.Equal(0, settings.SpeakerId);
    }

    [Fact]
    public void Unreadable_path_gives_defaults()
    {
        Directory.CreateDirectory(File_);
        var settings = Settings.Load(File_);
        Assert.Equal("kokoro-en-v0_19", settings.ModelId);
        Assert.Equal(1, settings.SpeakerId);
    }

    [Fact]
    public void ToVoice_clamps_speed()
    {
        Assert.Equal(2.0f, new Settings { Speed = 9f }.ToVoice().Speed);
        Assert.Equal(0.5f, new Settings { Speed = 0.1f }.ToVoice().Speed);
    }

    [Fact]
    public void Theme_and_highlight_default_and_round_trip()
    {
        var defaults = Settings.Load(File_);
        Assert.Equal(("system", "yellow"), (defaults.Theme, defaults.Highlight));

        new Settings { Theme = "sepia", Highlight = "blue" }.Save(File_);
        var loaded = Settings.Load(File_);
        Assert.Equal(("sepia", "blue"), (loaded.Theme, loaded.Highlight));
    }

    [Fact]
    public void Null_or_empty_theme_and_highlight_fall_back_and_unknown_ids_are_kept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{\"Theme\":null,\"Highlight\":\"\"}");
        var blank = Settings.Load(File_);
        Assert.Equal(("system", "yellow"), (blank.Theme, blank.Highlight));

        File.WriteAllText(File_, "{\"Theme\":\"neon\",\"Highlight\":\"teal\"}");
        var unknown = Settings.Load(File_);
        Assert.Equal(("neon", "teal"), (unknown.Theme, unknown.Highlight));
    }

    [Fact]
    public void Visualizer_defaults_round_trips_and_falls_back_when_blank()
    {
        Assert.Equal("orb", Settings.Load(File_).Visualizer);

        new Settings { Visualizer = "swarm" }.Save(File_);
        Assert.Equal("swarm", Settings.Load(File_).Visualizer);

        File.WriteAllText(File_, "{\"Visualizer\":null}");
        Assert.Equal("orb", Settings.Load(File_).Visualizer);
    }

    [Fact]
    public void Replies_defaults_to_off_round_trips_and_falls_back_when_blank()
    {
        Assert.Equal("off", Settings.Load(File_).Replies);

        new Settings { Replies = "all" }.Save(File_);
        Assert.Equal("all", Settings.Load(File_).Replies);

        File.WriteAllText(File_, "{\"Replies\":null}");
        Assert.Equal("off", Settings.Load(File_).Replies);
    }

    [Fact]
    public void When_playing_defaults_to_wait_round_trips_and_falls_back_when_blank()
    {
        Assert.Equal("wait", Settings.Load(File_).WhenPlaying);

        new Settings { WhenPlaying = "interrupt" }.Save(File_);
        Assert.Equal("interrupt", Settings.Load(File_).WhenPlaying);

        File.WriteAllText(File_, "{\"WhenPlaying\":null}");
        Assert.Equal("wait", Settings.Load(File_).WhenPlaying);
    }

    [Theory]
    [InlineData("switch", "tab", "wait")]
    [InlineData("queue", "tab", "wait")]
    [InlineData("finish", "tab", "wait")]
    [InlineData("off", "off", "wait")]
    public void The_replaced_Claudes_replies_modes_are_translated(string replies, string autoRead, string whenPlaying)
    {
        new Settings().Save(File_);
        File.WriteAllText(File_, $"{{\"Replies\":\"{replies}\"}}");

        var settings = Settings.Load(File_);

        Assert.Equal((autoRead, whenPlaying), (settings.Replies, settings.WhenPlaying));
    }

    [Fact]
    public void Text_options_default_round_trip_and_fall_back()
    {
        var defaults = Settings.Load(File_);
        Assert.Equal(
            (100, "segoe", "medium", "normal"),
            (defaults.TextSize, defaults.Font, defaults.ColumnWidth, defaults.LineSpacing));

        new Settings { TextSize = 150, Font = "georgia", ColumnWidth = "wide", LineSpacing = "relaxed" }.Save(File_);
        var loaded = Settings.Load(File_);
        Assert.Equal(
            (150, "georgia", "wide", "relaxed"),
            (loaded.TextSize, loaded.Font, loaded.ColumnWidth, loaded.LineSpacing));

        File.WriteAllText(File_, "{\"TextSize\":0,\"Font\":null,\"ColumnWidth\":\"\",\"LineSpacing\":null}");
        var blank = Settings.Load(File_);
        Assert.Equal(
            (100, "segoe", "medium", "normal"),
            (blank.TextSize, blank.Font, blank.ColumnWidth, blank.LineSpacing));
    }
}
