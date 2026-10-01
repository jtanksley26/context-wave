namespace MdReader.Core;

/// <summary>Mono float PCM.</summary>
public sealed record AudioClip(float[] Samples, int SampleRate);

public sealed record VoiceSettings(string ModelId, int SpeakerId, float Speed);

public interface ITtsEngine
{
    Task<AudioClip> SynthesizeAsync(string text, VoiceSettings voice, CancellationToken ct);
}

public interface IAudioOutput
{
    /// <summary>Completes when the clip has finished playing. Throws if cancelled.</summary>
    Task PlayAsync(AudioClip clip, CancellationToken ct);
    void Pause();
    void Resume();
}
