using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using ContextWave.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ContextWave.App;

public sealed class NAudioOutput : IAudioOutput, IPlaybackProbe, IDisposable
{
    private readonly object _lock = new();
    private WasapiOut? _current;
    private AudioClip? _clip;

    /// <summary>
    /// Added to the reported position, to line the visualiser up with what is heard. Positive
    /// shows the sound earlier, negative later.
    /// </summary>
    private const int LatencyOffsetMs = 0;

    public async Task PlayAsync(AudioClip clip, CancellationToken ct)
    {
        // WasapiOut stops without draining, which can clip the end; pad with 120 ms of silence.
        var source = new ClipProvider(clip, clip.SampleRate * 120 / 1000);
        using var device = new WasapiOut(AudioClientShareMode.Shared, 50);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) done.TrySetException(e.Exception);
            else done.TrySetResult();
        };
        device.Init(new SampleToWaveProvider(source));

        lock (_lock)
        {
            // Cancelled already means its tab was halted, and the next tab may be playing by now.
            ct.ThrowIfCancellationRequested();
            _current = device;
            _clip = clip;
        }
        try
        {
            using var registration = ct.Register(device.Stop);
            ct.ThrowIfCancellationRequested();
            device.Play();
            // Stop() is a no-op before Play(), so a cancel that landed in between was lost.
            if (ct.IsCancellationRequested) device.Stop();
            await done.Task;
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_lock)
            {
                // Every tab plays through this output, and a halted tab's playback can end after the
                // next tab's has started; only the playback that is still current clears it.
                if (_current == device)
                {
                    _current = null;
                    _clip = null;
                }
            }
        }
    }

    public bool TryGetPlayback([NotNullWhen(true)] out AudioClip? clip, out int samplePosition)
    {
        clip = null;
        samplePosition = 0;
        lock (_lock)
        {
            if (_clip is null || _current is not { PlaybackState: PlaybackState.Playing } device) return false;
            try
            {
                // GetPosition counts bytes in the device's own format, not the clip's.
                var played = AudioAnalyzer.SamplePosition(
                    device.GetPosition(), device.OutputWaveFormat.AverageBytesPerSecond, _clip.SampleRate);
                clip = _clip;
                samplePosition = played + _clip.SampleRate * LatencyOffsetMs / 1000;
                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException
                                           or NullReferenceException)
            {
                // The device went away between the state check and the read.
                return false;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_current?.PlaybackState == PlaybackState.Playing) _current.Pause();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_current?.PlaybackState == PlaybackState.Paused) _current.Play();
        }
    }

    public void Dispose()
    {
        lock (_lock) _current?.Stop();
    }

    /// <summary>
    /// Plays a clip's samples where they are, then <paramref name="padding"/> samples of silence.
    /// A copy into a byte buffer would put a second clip-sized array on the large object heap for
    /// every sentence.
    /// </summary>
    private sealed class ClipProvider(AudioClip clip, int padding) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, clip.Samples.Length + padding - _position);
            if (read <= 0) return 0;
            var fromClip = Math.Clamp(clip.Samples.Length - _position, 0, read);
            Array.Copy(clip.Samples, _position, buffer, offset, fromClip);
            Array.Clear(buffer, offset + fromClip, read - fromClip);
            _position += read;
            return read;
        }
    }
}
