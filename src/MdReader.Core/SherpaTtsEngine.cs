using SherpaOnnx;

namespace MdReader.Core;

public sealed class SherpaTtsEngine(ModelStore store) : ITtsEngine, IDisposable
{
    /// <summary>
    /// The fastest the voice models are asked to speak; anything faster comes from
    /// <see cref="TimeStretch"/>. Above normal speed they shorten each sound in whole steps and cut
    /// the shortest ones (Kokoro asked for 1.5x gave 1.57x and slurred), and Piper stops getting faster
    /// past about 1.5x. Stretching speech at its natural speed measured smoother than stretching speech
    /// the model had already sped up.
    /// </summary>
    public const float MaxModelSpeed = 1.0f;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private string? _loadedModelId;
    private bool _disposed;

    public async Task<AudioClip> SynthesizeAsync(string text, VoiceSettings voice, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) throw new OperationCanceledException();
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var tts = GetOrLoad(voice.ModelId);
                // Sped up, the models garble speech, so speeding up comes from stretching instead.
                var modelSpeed = Math.Min(voice.Speed, MaxModelSpeed);
                var audio = tts.Generate(text, modelSpeed, voice.SpeakerId);
                try
                {
                    var samples = TimeStretch.SpeedUp(audio.Samples, audio.SampleRate, voice.Speed / modelSpeed);
                    return new AudioClip(samples, audio.SampleRate);
                }
                finally
                {
                    audio.Dispose();
                }
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // If a synthesis is still running after 10 s the process is exiting anyway;
        // leaking is safer than freeing native memory that is in use.
        if (!_gate.Wait(TimeSpan.FromSeconds(10))) return;
        try
        {
            _disposed = true;
            _tts?.Dispose();
            _tts = null;
            _loadedModelId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private OfflineTts GetOrLoad(string modelId)
    {
        var model = VoiceCatalog.Find(modelId);
        if (_tts is not null && _loadedModelId == model.Id) return _tts;
        if (!store.IsInstalled(model))
            throw new InvalidOperationException($"Voice not ready: the {model.DisplayName} voice is not downloaded.");

        var dir = store.GetModelDir(model);
        var incomplete = !File.Exists(Path.Combine(dir, "tokens.txt"))
                         || !Directory.Exists(Path.Combine(dir, "espeak-ng-data"))
                         || (model.Kind == "kokoro" && !File.Exists(Path.Combine(dir, "voices.bin")));
        if (incomplete)
            throw new InvalidOperationException($"Voice not ready: the {model.DisplayName} voice files are incomplete.");

        var config = new OfflineTtsConfig();
        if (model.Kind == "kokoro")
        {
            config.Model.Kokoro.Model = Path.Combine(dir, model.ModelFile);
            config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");
        }
        else
        {
            config.Model.Vits.Model = Path.Combine(dir, model.ModelFile);
            config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(dir, "espeak-ng-data");
        }
        config.Model.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.Model.Provider = "cpu";

        _tts?.Dispose();
        _tts = null;
        _loadedModelId = null;
        _tts = new OfflineTts(config);
        _loadedModelId = model.Id;
        return _tts;
    }
}
