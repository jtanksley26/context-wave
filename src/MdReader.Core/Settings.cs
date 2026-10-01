using System.Text.Json;

namespace MdReader.Core;

public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ModelId { get; set; } = "kokoro-en-v0_19";
    public int SpeakerId { get; set; } = 1;
    public float Speed { get; set; } = 1.0f;
    public bool AnnounceCodeBlocks { get; set; } = true;

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
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
