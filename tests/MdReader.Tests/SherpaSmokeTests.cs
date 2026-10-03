using MdReader.Core;

namespace MdReader.Tests;

public class SherpaSmokeTests
{
    [Fact]
    [Trait("Category", "Manual")]
    public async Task Piper_voice_downloads_and_produces_audio()
    {
        // Not AppPaths.Models: the test run redirects MDREADER_HOME to a temp folder, and a manual
        // run should install the voice where the app looks for it.
        var models = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader\\models");
        var store = new ModelStore(models, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        if (!store.IsInstalled(VoiceCatalog.Piper))
            await store.InstallAsync(VoiceCatalog.Piper, null, CancellationToken.None);

        using var engine = new SherpaTtsEngine(store);
        var clip = await engine.SynthesizeAsync(
            "Hello from Context Wave.", new VoiceSettings(VoiceCatalog.Piper.Id, 0, 1.0f), CancellationToken.None);

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

    [Fact]
    public async Task Synthesize_after_Dispose_is_cancelled()
    {
        var empty = Directory.CreateTempSubdirectory("mdreader-disposed-").FullName;
        try
        {
            var engine = new SherpaTtsEngine(new ModelStore(empty, new HttpClient()));
            engine.Dispose();
            engine.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SynthesizeAsync(
                "Hi.", new VoiceSettings(VoiceCatalog.Piper.Id, 0, 1f), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task Incomplete_model_folder_reports_voice_not_ready()
    {
        var root = Directory.CreateTempSubdirectory("mdreader-incomplete-").FullName;
        try
        {
            var dir = Path.Combine(root, "vits-piper-en_US-lessac-medium");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "en_US-lessac-medium.onnx"), []);
            using var engine = new SherpaTtsEngine(new ModelStore(root, new HttpClient()));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SynthesizeAsync(
                "Hi.", new VoiceSettings(VoiceCatalog.Piper.Id, 0, 1f), CancellationToken.None));
            Assert.Contains("not ready", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
