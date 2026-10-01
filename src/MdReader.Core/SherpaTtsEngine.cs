using SherpaOnnx;

namespace MdReader.Core;

public sealed class SherpaTtsEngine(ModelStore store) : ITtsEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private string? _loadedModelId;

    public async Task<AudioClip> SynthesizeAsync(string text, VoiceSettings voice, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var tts = GetOrLoad(voice.ModelId);
                var audio = tts.Generate(text, voice.Speed, voice.SpeakerId);
                try
                {
                    return new AudioClip(audio.Samples, audio.SampleRate);
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
        _tts?.Dispose();
        _tts = null;
    }

    private OfflineTts GetOrLoad(string modelId)
    {
        var model = VoiceCatalog.Find(modelId);
        if (_tts is not null && _loadedModelId == model.Id) return _tts;
        if (!store.IsInstalled(model))
            throw new InvalidOperationException($"Voice not ready: the {model.DisplayName} voice is not downloaded.");

        var dir = store.GetModelDir(model);
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
        _tts = new OfflineTts(config);
        _loadedModelId = model.Id;
        return _tts;
    }
}
