using System.Text.Json;

namespace MdReader.Core;

public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ModelId { get; set; } = "kokoro-en-v0_19";
    public int SpeakerId { get; set; } = 1;
    public float Speed { get; set; } = 1.0f;
    public bool AnnounceCodeBlocks { get; set; } = true;
    public string Theme { get; set; } = ThemeCatalog.SystemId;
    public string Highlight { get; set; } = ThemeCatalog.DefaultHighlightId;

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
                if (string.IsNullOrEmpty(loaded.ModelId)) loaded.ModelId = "kokoro-en-v0_19";
                if (loaded.SpeakerId < 0) loaded.SpeakerId = 0;
                // Unknown ids are kept as written; ThemeCatalog.Resolve treats them as the defaults.
                if (string.IsNullOrEmpty(loaded.Theme)) loaded.Theme = ThemeCatalog.SystemId;
                if (string.IsNullOrEmpty(loaded.Highlight)) loaded.Highlight = ThemeCatalog.DefaultHighlightId;
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public VoiceSettings ToVoice() => new(ModelId, SpeakerId, Math.Clamp(Speed, 0.5f, 2.0f));
}
