using System.IO;
using MdReader.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MdReader.App;

public sealed class NAudioOutput : IAudioOutput, IDisposable
{
    private readonly object _lock = new();
    private WasapiOut? _current;

    public async Task PlayAsync(AudioClip clip, CancellationToken ct)
    {
        var bytes = new byte[clip.Samples.Length * sizeof(float)];
        Buffer.BlockCopy(clip.Samples, 0, bytes, 0, bytes.Length);
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

        lock (_lock) _current = device;
        try
        {
            using var registration = ct.Register(device.Stop);
            ct.ThrowIfCancellationRequested();
            device.Play();
            await done.Task;
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_lock) _current = null;
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
