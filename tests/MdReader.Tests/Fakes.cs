using System.Collections.Concurrent;
using MdReader.Core;

namespace MdReader.Tests;

internal sealed class FakeTts : ITtsEngine
{
    public ConcurrentQueue<(string Text, VoiceSettings Voice)> Calls { get; } = new();
    public HashSet<string> FailOn { get; } = [];

    public Task<AudioClip> SynthesizeAsync(string text, VoiceSettings voice, CancellationToken ct)
    {
        Calls.Enqueue((text, voice));
        return FailOn.Contains(text)
            ? Task.FromException<AudioClip>(new InvalidOperationException("synth failed"))
            : Task.FromResult(new AudioClip([text.Length], 24000));
    }
}

internal sealed class FakeOutput : IAudioOutput
{
    private readonly object _lock = new();
    private TaskCompletionSource? _hold;

    public List<AudioClip> Played { get; } = [];
    public int FailuresRemaining { get; set; }

    /// <summary>When true, each PlayAsync stays pending until Release() is called.</summary>
    public bool Manual { get; set; }

    public bool Pending
    {
        get { lock (_lock) return _hold is { Task.IsCompleted: false }; }
    }

    public Task PlayAsync(AudioClip clip, CancellationToken ct)
    {
        lock (_lock)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromException(new InvalidOperationException("no audio device"));
            }
            Played.Add(clip);
            if (!Manual) return Task.CompletedTask;

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => hold.TrySetCanceled(ct));
            _hold = hold;
            return hold.Task;
        }
    }

    public void Release()
    {
        lock (_lock) _hold?.TrySetResult();
    }

    public void Pause() { }
    public void Resume() { }
}
