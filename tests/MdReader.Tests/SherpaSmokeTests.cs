using MdReader.Core;

namespace MdReader.Tests;

public class SherpaSmokeTests
{
    [Fact]
    [Trait("Category", "Manual")]
    public async Task Piper_voice_downloads_and_produces_audio()
    {
        var store = new ModelStore(AppPaths.Models, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        if (!store.IsInstalled(VoiceCatalog.Piper))
            await store.InstallAsync(VoiceCatalog.Piper, null, CancellationToken.None);

        using var engine = new SherpaTtsEngine(store);
        var clip = await engine.SynthesizeAsync(
            "Hello from MD Reader.", new VoiceSettings(VoiceCatalog.Piper.Id, 0, 1.0f), CancellationToken.None);

        Assert.True(clip.SampleRate > 0);
        Assert.True(clip.Samples.Length > clip.SampleRate / 2, "expected at least half a second of audio");
    }

    [Fact]
    public async Task Missing_model_reports_voice_not_ready()
    {
        var empty = Directory.CreateTempSubdirectory("mdreader-empty-").FullName;
        using var engine = new SherpaTtsEngine(new ModelStore(empty, new HttpClient()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SynthesizeAsync(
            "Hi.", new VoiceSettings(VoiceCatalog.Kokoro.Id, 0, 1f), CancellationToken.None));
        Assert.Contains("not ready", ex.Message);
        Directory.Delete(empty, recursive: true);
    }
}
