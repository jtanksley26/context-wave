using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using MdReader.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MdReader.App;

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
        var padding = clip.SampleRate * 120 / 1000;
        var bytes = new byte[(clip.Samples.Length + padding) * sizeof(float)];
        Buffer.BlockCopy(clip.Samples, 0, bytes, 0, clip.Samples.Length * sizeof(float));
        using var stream = new RawSourceWaveStream(
            new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, 1));
        using var device = new WasapiOut(AudioClientShareMode.Shared, 50);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) done.TrySetException(e.Exception);
            else done.TrySetResult();
        };
        device.Init(stream);

        lock (_lock)
        {
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
                _current = null;
                _clip = null;
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
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
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
}
