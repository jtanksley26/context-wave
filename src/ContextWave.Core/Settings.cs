using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextWave.Core;

public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ModelId { get; set; } = VoiceCatalog.Kokoro.Id;
    public int SpeakerId { get; set; } = 1;
    public float Speed { get; set; } = 1.0f;
    public bool AnnounceCodeBlocks { get; set; } = true;
    public string Theme { get; set; } = ThemeCatalog.SystemId;
    public string Highlight { get; set; } = ThemeCatalog.DefaultHighlightId;
    public string Visualizer { get; set; } = VisualizerCatalog.DefaultId;
    /// <summary>Auto Read: a <see cref="ReplyMode"/>.</summary>
    public string Replies { get; set; } = ReplyMode.Off;

    /// <summary>When something is playing: a <see cref="Core.WhenPlaying"/>.</summary>
    public string WhenPlaying { get; set; } = Core.WhenPlaying.Wait;

    public int TextSize { get; set; } = TextOptions.DefaultSize;
    public string Font { get; set; } = TextOptions.DefaultFontId;
    public string ColumnWidth { get; set; } = TextOptions.DefaultWidthId;
    public string LineSpacing { get; set; } = TextOptions.DefaultSpacingId;

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = Parse(File.ReadAllText(path));
                if (string.IsNullOrEmpty(loaded.ModelId)) loaded.ModelId = VoiceCatalog.Kokoro.Id;
                if (loaded.SpeakerId < 0) loaded.SpeakerId = 0;
                // Unknown ids are kept as written; ThemeCatalog.Resolve treats them as the defaults.
                if (string.IsNullOrEmpty(loaded.Theme)) loaded.Theme = ThemeCatalog.SystemId;
                if (string.IsNullOrEmpty(loaded.Highlight)) loaded.Highlight = ThemeCatalog.DefaultHighlightId;
                if (string.IsNullOrEmpty(loaded.Visualizer)) loaded.Visualizer = VisualizerCatalog.DefaultId;
                // The "Claude's replies" modes this replaced. None of them ever cut off a file or
                // a speak stream, which Interrupt does, so all of them wait.
                (loaded.Replies, loaded.WhenPlaying) = loaded.Replies switch
                {
                    "switch" or "queue" or "finish" => (ReplyMode.Tab, Core.WhenPlaying.Wait),
                    _ => (loaded.Replies, loaded.WhenPlaying),
                };
                if (string.IsNullOrEmpty(loaded.Replies)) loaded.Replies = ReplyMode.Off;
                if (string.IsNullOrEmpty(loaded.WhenPlaying)) loaded.WhenPlaying = Core.WhenPlaying.Wait;
                if (loaded.TextSize <= 0) loaded.TextSize = TextOptions.DefaultSize;
                if (string.IsNullOrEmpty(loaded.Font)) loaded.Font = TextOptions.DefaultFontId;
                if (string.IsNullOrEmpty(loaded.ColumnWidth)) loaded.ColumnWidth = TextOptions.DefaultWidthId;
                if (string.IsNullOrEmpty(loaded.LineSpacing)) loaded.LineSpacing = TextOptions.DefaultSpacingId;
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    private static Settings Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch (JsonException)
        {
        }

        // One value of the wrong type, say from a hand edit, would otherwise lose every setting, and the
        // next save would write the defaults over them. Keep each value that still reads.
        var settings = new Settings();
        if (JsonNode.Parse(json) is not JsonObject values) return settings;
        foreach (var property in typeof(Settings).GetProperties())
        {
            if (!property.CanWrite || !values.TryGetPropertyValue(property.Name, out var value)) continue;
            try
            {
                property.SetValue(settings, value.Deserialize(property.PropertyType));
            }
            catch (JsonException)
            {
            }
        }
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write beside the file and swap it in, so a crash cannot leave the settings half-written.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    public VoiceSettings ToVoice() => new(ModelId, SpeakerId, Math.Clamp(Speed, 0.5f, 2.0f));
}
