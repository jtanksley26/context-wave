namespace MdReader.Core;

public enum ReadingState { Idle, Playing, Paused }

public sealed class ReadingQueue
{
    public const int LookAhead = 3;

    private readonly object _gate = new();
    private readonly ITtsEngine _tts;
    private readonly IAudioOutput _output;
    private readonly Func<VoiceSettings> _voice;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly List<Sentence> _sentences = [];
    private readonly Dictionary<int, Task<AudioClip>> _clips = [];
    private CancellationTokenSource _synthCts = new();
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource? _pause;
    private Task _run = Task.CompletedTask;
    private ReadingState _state = ReadingState.Idle;
    private int _index;

    public ReadingQueue(
        ITtsEngine tts,
        IAudioOutput output,
        Func<VoiceSettings> voice,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _tts = tts;
        _output = output;
        _voice = voice;
        _delay = delay ?? Task.Delay;
    }

    public event Action<int>? SentenceStarted;
    public event Action<ReadingState>? StateChanged;
    public event Action? Finished;
    public event Action<int, Exception>? SentenceFailed;
    public event Action<Exception>? PlaybackFailed;

    public ReadingState State { get { lock (_gate) return _state; } }
    public int CurrentIndex { get { lock (_gate) return _index; } }
    public int Count { get { lock (_gate) return _sentences.Count; } }

    public void Load(IReadOnlyList<Sentence> sentences, bool autoPlay = true)
    {
        lock (_gate)
        {
            ResetLocked();
            _sentences.AddRange(sentences);
            if (autoPlay && _sentences.Count > 0) StartLocked(0);
        }
        RaiseState();
    }

    public void Append(IReadOnlyList<Sentence> sentences, bool autoPlay = true)
    {
        if (sentences.Count == 0) return;
        lock (_gate)
        {
            var firstNew = _sentences.Count;
            _sentences.AddRange(sentences);
            if (autoPlay && _state == ReadingState.Idle) StartLocked(firstNew);
        }
        RaiseState();
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_state == ReadingState.Paused)
            {
                _state = ReadingState.Playing;
                _output.Resume();
                _pause?.TrySetResult();
                _pause = null;
            }
            else if (_state == ReadingState.Idle && _sentences.Count > 0)
            {
                StartLocked(_index < _sentences.Count ? _index : 0);
            }
        }
        RaiseState();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != ReadingState.Playing) return;
            _state = ReadingState.Paused;
            _pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _output.Pause();
        }
        RaiseState();
    }

    public void Next() => Move(+1);

    public void Previous() => Move(-1);

    public bool JumpTo(int sentenceId)
    {
        lock (_gate)
        {
            var index = _sentences.FindIndex(s => s.Id == sentenceId);
            if (index < 0) return false;
            StartLocked(index);
        }
        RaiseState();
        return true;
    }

    public void Stop()
    {
        lock (_gate) ResetLocked();
        RaiseState();
    }

    /// <summary>Drops synthesized audio so later sentences pick up the current voice settings.</summary>
    public void InvalidateCache()
    {
        CancellationTokenSource stale;
        lock (_gate)
        {
            stale = _synthCts;
            _synthCts = new CancellationTokenSource();
            _clips.Clear();
        }
        // Cancel outside the lock and after the swap: cancellation can resume the run loop
        // inline on this thread, and it must then see the new token and raise events unlocked.
        stale.Cancel();
    }

    private void Move(int delta)
    {
        lock (_gate)
        {
            if (_sentences.Count == 0) return;
            var target = Math.Max(0, _index + delta);
            if (target >= _sentences.Count)
            {
                CancelRunLocked();
                _index = _sentences.Count;
                _state = ReadingState.Idle;
            }
            else
            {
                StartLocked(target);
            }
        }
        RaiseState();
    }

    private void RaiseState()
    {
        var state = State;
        Raise(() => StateChanged?.Invoke(state));
    }

    /// <summary>Raises an event so that a throwing subscriber is logged instead of killing the caller.</summary>
    private static void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            FileLog.Write($"Event handler failed: {ex}");
        }
    }

    private void ResetLocked()
    {
        CancelRunLocked();
        _sentences.Clear();
        _clips.Clear();
        // The run is already cancelled, so cancelling under the lock cannot resume it;
        // swap first so nothing can pick up the cancelled token.
        var stale = _synthCts;
        _synthCts = new CancellationTokenSource();
        stale.Cancel();
        _index = 0;
        _state = ReadingState.Idle;
    }

    private void CancelRunLocked()
    {
        _runCts?.Cancel();
        _runCts = null;
        if (_pause is null) return;
        _output.Resume();
        _pause.TrySetResult();
        _pause = null;
    }

    private void StartLocked(int index)
    {
        CancelRunLocked();
        DropStaleClipsLocked(index);
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        _index = index;
        _state = ReadingState.Playing;
        var previous = _run;
        _run = Task.Run(async () =>
        {
            try { await previous; } catch { /* the previous run reports its own errors */ }
            await RunAsync(index, ct);
        });
    }

    /// <summary>
    /// After a move, abandons look-ahead work for the old position so the target sentence
    /// does not wait behind it in the engine and the clips do not stay cached forever.
    /// </summary>
    private void DropStaleClipsLocked(int index)
    {
        var window = new HashSet<int>();
        for (var k = index; k <= index + LookAhead && k < _sentences.Count; k++)
            window.Add(_sentences[k].Id);
        if (_clips.Keys.All(window.Contains)) return;

        // All syntheses share one token, so the pending ones inside the window are restarted too.
        foreach (var id in _clips.Keys.ToList())
        {
            if (!window.Contains(id) || !_clips[id].IsCompletedSuccessfully) _clips.Remove(id);
        }
        // The run is already cancelled (CancelRunLocked), so cancelling under the lock cannot
        // resume it; swap first so nothing can pick up the cancelled token.
        var stale = _synthCts;
        _synthCts = new CancellationTokenSource();
        stale.Cancel();
    }

    private void EnsureClipLocked(Sentence sentence)
    {
        if (_clips.ContainsKey(sentence.Id)) return;
        try
        {
            _clips[sentence.Id] = _tts.SynthesizeAsync(sentence.SpokenText, _voice(), _synthCts.Token);
        }
        catch (Exception ex)
        {
            _clips[sentence.Id] = Task.FromException<AudioClip>(ex);
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? gate;
            lock (_gate) gate = _pause?.Task;
            if (gate is null) return;
            await gate.WaitAsync(ct);
        }
    }

    private async Task RunAsync(int index, CancellationToken ct)
    {
        var i = index;
        while (true)
        {
            Sentence? sentence = null;
            Task<AudioClip>? clipTask = null;
            lock (_gate)
            {
                if (ct.IsCancellationRequested) return;
                if (i >= _sentences.Count)
                {
                    _index = _sentences.Count;
                    _state = ReadingState.Idle;
                    _runCts = null;
                }
                else
                {
                    _index = i;
                    sentence = _sentences[i];
                    for (var k = i; k <= i + LookAhead && k < _sentences.Count; k++)
                        EnsureClipLocked(_sentences[k]);
                    clipTask = _clips[sentence.Id];
                }
            }

            if (sentence is null || clipTask is null)
            {
                Raise(() => StateChanged?.Invoke(ReadingState.Idle));
                Raise(() => Finished?.Invoke());
                return;
            }

            AudioClip clip;
            try
            {
                clip = await clipTask.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
                // The cache was invalidated under us: synthesize this sentence again.
                lock (_gate)
                {
                    if (_clips.TryGetValue(sentence.Id, out var cached) && cached == clipTask)
                        _clips.Remove(sentence.Id);
                }
                continue;
            }
            catch (Exception ex)
            {
                lock (_gate) _clips.Remove(sentence.Id);
                var failedId = sentence.Id;
                Raise(() => SentenceFailed?.Invoke(failedId, ex));
                i++;
                continue;
            }

            try
            {
                await WaitWhilePausedAsync(ct);
                ct.ThrowIfCancellationRequested();
                var startedId = sentence.Id;
                Raise(() => SentenceStarted?.Invoke(startedId));
                var playing = _output.PlayAsync(clip, ct);
                // Pause() may have run after the wait above but before the output had anything
                // to pause; apply it now that playback has started.
                lock (_gate) { if (_pause is not null) _output.Pause(); }
                await playing;
                if (sentence.PauseAfterMs > 0) await _delay(sentence.PauseAfterMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failure caused by cancelling this run is not an audio problem.
                if (ct.IsCancellationRequested) return;
                // Audio output problem: pause on this sentence; Play() retries it.
                var paused = false;
                lock (_gate)
                {
                    if (!ct.IsCancellationRequested && _state == ReadingState.Playing)
                    {
                        _state = ReadingState.Paused;
                        _pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        paused = true;
                    }
                }
                if (paused) Raise(() => StateChanged?.Invoke(ReadingState.Paused));
                Raise(() => PlaybackFailed?.Invoke(ex));
                continue;
            }

            lock (_gate) _clips.Remove(sentence.Id);
            i++;
        }
    }
}
