using System.Text.Json;

namespace ContextWave.Core;

public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ModelId { get; set; } = "kokoro-en-v0_19";
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
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
                if (string.IsNullOrEmpty(loaded.ModelId)) loaded.ModelId = "kokoro-en-v0_19";
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

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public VoiceSettings ToVoice() => new(ModelId, SpeakerId, Math.Clamp(Speed, 0.5f, 2.0f));
}
