using MdReader.Core;

namespace MdReader.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdreader-settings-").FullName;
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
}
