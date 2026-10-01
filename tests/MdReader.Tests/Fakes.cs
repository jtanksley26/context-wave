using System.Collections.Concurrent;
using MdReader.Core;

namespace MdReader.Tests;

internal sealed class FakeTts : ITtsEngine
{
    private sealed class GatedCall(string text)
    {
        public string Text { get; } = text;
        public TaskCompletionSource<AudioClip> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Cancelled;
    }

    private readonly object _lock = new();
    private readonly List<GatedCall> _gated = [];

    public ConcurrentQueue<(string Text, VoiceSettings Voice)> Calls { get; } = new();
    public HashSet<string> FailOn { get; } = [];

    /// <summary>When true, each SynthesizeAsync stays pending until Release(text) is called or its token is cancelled.</summary>
    public bool Gated { get; set; }

    /// <summary>Number of gated calls that are neither released nor cancelled.</summary>
    public int PendingCount
    {
        get { lock (_lock) return _gated.Count(c => !c.Completion.Task.IsCompleted); }
    }

    public int CallCount(string text) => Calls.Count(c => c.Text == text);

    /// <summary>True when at least one gated call for this text exists and all of them were cancelled.</summary>
    public bool WasCancelled(string text)
    {
        lock (_lock)
        {
            var calls = _gated.Where(c => c.Text == text).ToList();
            return calls.Count > 0 && calls.All(c => c.Cancelled);
        }
    }

    /// <summary>Completes every pending gated call for this text.</summary>
    public void Release(string text)
    {
        List<GatedCall> calls;
        lock (_lock) calls = _gated.Where(c => c.Text == text).ToList();
        foreach (var call in calls) call.Completion.TrySetResult(new AudioClip([text.Length], 24000));
    }

    public Task<AudioClip> SynthesizeAsync(string text, VoiceSettings voice, CancellationToken ct)
    {
        Calls.Enqueue((text, voice));
        if (FailOn.Contains(text))
            return Task.FromException<AudioClip>(new InvalidOperationException("synth failed"));
        if (!Gated) return Task.FromResult(new AudioClip([text.Length], 24000));

        var call = new GatedCall(text);
        lock (_lock) _gated.Add(call);
        ct.Register(() =>
        {
            if (call.Completion.TrySetCanceled(ct)) call.Cancelled = true;
        });
        return call.Completion.Task;
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

    public int PauseCalls { get; private set; }
    public int ResumeCalls { get; private set; }

    /// <summary>True once Pause() has been called while a clip was pending (i.e. applied to a playing clip).</summary>
    public bool PausedWhilePending { get; private set; }

    public void Pause()
    {
        lock (_lock)
        {
            PauseCalls++;
            if (_hold is { Task.IsCompleted: false }) PausedWhilePending = true;
        }
    }

    public void Resume()
    {
        lock (_lock) ResumeCalls++;
    }
}
