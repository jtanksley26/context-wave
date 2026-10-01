using System.Collections.Concurrent;
using MdReader.Core;

namespace MdReader.Tests;

public class ReadingQueueTests
{
    private readonly FakeTts _tts = new();
    private readonly FakeOutput _output = new();
    private readonly ConcurrentQueue<int> _started = new();
    private float _speed = 1f;

    private static Sentence[] Make(int count, int firstId = 0) =>
        Enumerable.Range(firstId, count).Select(i => new Sentence(i, $"Sentence {i}.", 0)).ToArray();

    private ReadingQueue Create()
    {
        var queue = new ReadingQueue(_tts, _output, () => new VoiceSettings("m", 0, _speed), (_, _) => Task.CompletedTask);
        queue.SentenceStarted += _started.Enqueue;
        return queue;
    }

    private static Task NextFinished(ReadingQueue queue)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler()
        {
            queue.Finished -= Handler;
            tcs.TrySetResult();
        }
        queue.Finished += Handler;
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Load_plays_every_sentence_in_order_then_finishes()
    {
        var queue = Create();
        var finished = NextFinished(queue);
        queue.Load(Make(3));
        await finished;

        Assert.Equal(new[] { 0, 1, 2 }, _started);
        Assert.Equal(3, _output.Played.Count);
        Assert.Equal(ReadingState.Idle, queue.State);
    }

    [Fact]
    public async Task Load_without_autoplay_stays_idle_until_Play()
    {
        var queue = Create();
        queue.Load(Make(2), autoPlay: false);
        Assert.Equal(ReadingState.Idle, queue.State);
        Assert.Empty(_started);

        var finished = NextFinished(queue);
        queue.Play();
        await finished;
        Assert.Equal(new[] { 0, 1 }, _started);
    }

    [Fact]
    public async Task Pause_holds_the_next_sentence_until_Play()
    {
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(3));
        await TestUtil.WaitUntil(() => _output.Pending);

        queue.Pause();
        _output.Release();
        await Task.Delay(100);
        Assert.Equal(new[] { 0 }, _started);
        Assert.Equal(ReadingState.Paused, queue.State);

        queue.Play();
        await TestUtil.WaitUntil(() => _started.Count == 2);
        Assert.Equal(ReadingState.Playing, queue.State);
        queue.Stop();
    }

    [Fact]
    public async Task JumpTo_continues_from_the_chosen_sentence()
    {
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(5));
        await TestUtil.WaitUntil(() => _output.Pending);

        var finished = NextFinished(queue);
        Assert.True(queue.JumpTo(3));
        await TestUtil.WaitUntil(() => _started.Contains(3) && _output.Pending);
        _output.Manual = false;
        _output.Release();
        await finished;

        Assert.Equal(new[] { 0, 3, 4 }, _started);
        Assert.False(queue.JumpTo(99));
    }

    [Fact]
    public async Task Next_and_Previous_move_one_sentence()
    {
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(3));
        await TestUtil.WaitUntil(() => _output.Pending);

        queue.Next();
        await TestUtil.WaitUntil(() => _started.LastOrDefault() == 1 && _output.Pending);
        queue.Previous();
        await TestUtil.WaitUntil(() => _started.Count == 3 && _output.Pending);

        Assert.Equal(new[] { 0, 1, 0 }, _started);
        queue.Stop();
    }

    [Fact]
    public async Task Append_while_idle_starts_at_the_new_sentence()
    {
        var queue = Create();
        var first = NextFinished(queue);
        queue.Load(Make(2));
        await first;

        var second = NextFinished(queue);
        queue.Append(Make(1, firstId: 2));
        await second;

        Assert.Equal(new[] { 0, 1, 2 }, _started);
    }

    [Fact]
    public async Task Append_while_playing_extends_the_run()
    {
        _output.Manual = true;
        var queue = Create();
        var finished = NextFinished(queue);
        queue.Load(Make(1));
        await TestUtil.WaitUntil(() => _output.Pending);

        queue.Append(Make(1, firstId: 1));
        _output.Manual = false;
        _output.Release();
        await finished;

        Assert.Equal(new[] { 0, 1 }, _started);
    }

    [Fact]
    public async Task Failed_synthesis_skips_the_sentence()
    {
        _tts.FailOn.Add("Sentence 1.");
        var queue = Create();
        var failed = new ConcurrentQueue<int>();
        queue.SentenceFailed += (id, _) => failed.Enqueue(id);
        var finished = NextFinished(queue);
        queue.Load(Make(3));
        await finished;

        Assert.Equal(new[] { 0, 2 }, _started);
        Assert.Equal(new[] { 1 }, failed);
    }

    [Fact]
    public async Task Playback_failure_pauses_and_Play_retries_the_sentence()
    {
        _output.FailuresRemaining = 1;
        var queue = Create();
        Exception? reported = null;
        queue.PlaybackFailed += ex => reported = ex;
        var finished = NextFinished(queue);
        queue.Load(Make(2));
        // PlaybackFailed is raised outside the lock, just after the state becomes Paused.
        await TestUtil.WaitUntil(() => queue.State == ReadingState.Paused && reported is not null);
        Assert.NotNull(reported);

        queue.Play();
        await finished;
        Assert.Equal(new[] { 0, 0, 1 }, _started);
        Assert.Equal(2, _output.Played.Count);
    }

    [Fact]
    public async Task Stop_clears_everything()
    {
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(3));
        await TestUtil.WaitUntil(() => _output.Pending);

        queue.Stop();
        Assert.Equal(ReadingState.Idle, queue.State);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Synthesizes_at_most_three_sentences_ahead()
    {
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(10));
        await TestUtil.WaitUntil(() => _output.Pending && _tts.Calls.Count == 4);
        await Task.Delay(50);

        Assert.Equal(4, _tts.Calls.Count);
        queue.Stop();
    }

    [Fact]
    public async Task InvalidateCache_resynthesizes_with_the_current_voice()
    {
        _output.Manual = true;
        var queue = Create();
        var finished = NextFinished(queue);
        queue.Load(Make(3));
        await TestUtil.WaitUntil(() => _output.Pending);

        _speed = 2f;
        queue.InvalidateCache();
        _output.Manual = false;
        _output.Release();
        await finished;

        Assert.Equal(new[] { 0, 1, 2 }, _started);
        Assert.Equal(2f, _tts.Calls.Last().Voice.Speed);
    }

    [Fact]
    public async Task Jump_cancels_stale_lookahead_synthesis()
    {
        _tts.Gated = true;
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(40));
        await TestUtil.WaitUntil(() => _tts.PendingCount == 4);

        Assert.True(queue.JumpTo(20));
        await TestUtil.WaitUntil(() =>
            Enumerable.Range(20, 4).All(i => _tts.CallCount($"Sentence {i}.") == 1));
        await TestUtil.WaitUntil(() =>
            Enumerable.Range(1, 3).All(i => _tts.WasCancelled($"Sentence {i}.")));

        Assert.All(Enumerable.Range(20, 4), i => Assert.False(_tts.WasCancelled($"Sentence {i}.")));
        Assert.Equal(8, _tts.Calls.Count);

        _tts.Release("Sentence 20.");
        await TestUtil.WaitUntil(() => _started.Contains(20) && _output.Pending);
        Assert.Equal(new[] { 20 }, _started);
        queue.Stop();
    }

    [Fact]
    public async Task Next_keeps_already_synthesized_neighbours()
    {
        _output.Manual = true;
        var queue = Create();
        queue.Load(Make(6));
        await TestUtil.WaitUntil(() => _output.Pending && _tts.Calls.Count == 4);

        queue.Next();
        await TestUtil.WaitUntil(() => _started.LastOrDefault() == 1 && _output.Pending);
        await TestUtil.WaitUntil(() => _tts.CallCount("Sentence 4.") == 1);

        Assert.Equal(1, _tts.CallCount("Sentence 1."));
        Assert.Equal(1, _tts.CallCount("Sentence 2."));
        Assert.Equal(1, _tts.CallCount("Sentence 3."));
        Assert.Equal(1, _tts.CallCount("Sentence 4."));
        Assert.Equal(5, _tts.Calls.Count);
        queue.Stop();
    }

    [Fact]
    public async Task Pause_requested_as_a_sentence_starts_is_applied_to_the_output()
    {
        _output.Manual = true;
        var queue = Create();
        queue.SentenceStarted += id =>
        {
            if (id == 0) queue.Pause();
        };
        queue.Load(Make(2));
        await TestUtil.WaitUntil(() => _output.Pending);

        // The pause arrived before the clip existed; it must be re-applied once the clip is playing.
        await TestUtil.WaitUntil(() => _output.PausedWhilePending);
        Assert.Equal(ReadingState.Paused, queue.State);

        queue.Play();
        Assert.True(_output.ResumeCalls >= 1);
        _output.Release();
        await TestUtil.WaitUntil(() => _started.Contains(1));
        Assert.Equal(new[] { 0, 1 }, _started);
        queue.Stop();
    }

    [Fact]
    public async Task Throwing_event_handler_does_not_stop_playback()
    {
        var queue = Create();
        queue.SentenceStarted += id =>
        {
            if (id == 0) throw new InvalidOperationException("handler failed");
        };
        var finished = NextFinished(queue);
        queue.Load(Make(3));
        await finished;

        Assert.Equal(new[] { 0, 1, 2 }, _started);
        Assert.Equal(3, _output.Played.Count);
    }

    [Fact]
    public async Task Three_consecutive_failures_pause_on_the_first_failed_sentence()
    {
        _tts.FailAll = true;
        var queue = Create();
        var failed = new ConcurrentQueue<int>();
        var playbackFailed = new ConcurrentQueue<Exception>();
        queue.SentenceFailed += (id, _) => failed.Enqueue(id);
        queue.PlaybackFailed += playbackFailed.Enqueue;
        queue.Load(Make(5));
        // PlaybackFailed is the last event of a stall, raised after the state becomes Paused.
        await TestUtil.WaitUntil(() => queue.State == ReadingState.Paused && !playbackFailed.IsEmpty);

        Assert.Equal(ReadingState.Paused, queue.State);
        Assert.Equal(0, queue.CurrentIndex);
        Assert.Single(playbackFailed);
        Assert.Equal(new[] { 0, 1, 2 }, failed);
        Assert.Empty(_started);
        Assert.Empty(_output.Played);
        queue.Stop();
    }

    [Fact]
    public async Task Play_after_a_failure_stall_retries_from_the_first_failed_sentence()
    {
        _tts.FailAll = true;
        var queue = Create();
        var playbackFailed = new ConcurrentQueue<Exception>();
        queue.PlaybackFailed += playbackFailed.Enqueue;
        var finished = NextFinished(queue);
        queue.Load(Make(5));
        await TestUtil.WaitUntil(() => queue.State == ReadingState.Paused && !playbackFailed.IsEmpty);

        _tts.FailAll = false;
        queue.Play();
        await finished;

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, _started);
        Assert.Single(playbackFailed);
        Assert.Equal(ReadingState.Idle, queue.State);
    }

    [Fact]
    public async Task Synthesis_failure_while_paused_is_retried_not_skipped()
    {
        _tts.Gated = true;
        var queue = Create();
        var failed = new ConcurrentQueue<int>();
        queue.SentenceFailed += (id, _) => failed.Enqueue(id);
        queue.Load(Make(3));
        await TestUtil.WaitUntil(() => _tts.PendingCount == 3);

        queue.Pause();
        _tts.Fault("Sentence 0.");
        // Nothing observable happens while the queue holds the failed sentence, so give the
        // run loop time to see the fault; a skip would show up as SentenceFailed here or below.
        await Task.Delay(150);
        Assert.Empty(failed);
        Assert.Equal(ReadingState.Paused, queue.State);
        Assert.Equal(0, queue.CurrentIndex);

        _tts.Gated = false;
        queue.Play();
        await TestUtil.WaitUntil(() => !_started.IsEmpty);

        Assert.Equal(0, _started.First());
        Assert.Empty(failed);
        Assert.Equal(2, _tts.CallCount("Sentence 0."));
        queue.Stop();
    }
}