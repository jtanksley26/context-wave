using ContextWave.App;
using ContextWave.Core;
using NAudio.Wave;

namespace ContextWave.Tests;

public class ClipWaveProviderTests
{
    [Fact]
    public void It_plays_the_samples_then_the_padding_then_ends()
    {
        var clip = new AudioClip([0.5f, -0.25f, 1f], 24000);
        var provider = new ClipWaveProvider(clip, padding: 2);
        var bytes = new byte[64];

        // Uneven reads, as the device asks for whatever its buffer has room for.
        var read = provider.Read(bytes, 0, 6);
        read += provider.Read(bytes, read, 50);
        var floats = new float[read / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, read);

        Assert.Equal(5 * sizeof(float), read);
        Assert.Equal([0.5f, -0.25f, 1f, 0f, 0f], floats);
        Assert.Equal(0, provider.Read(bytes, 0, 16));
    }

    [Fact]
    public void Reads_after_the_clip_has_run_out_give_the_padding()
    {
        var provider = new ClipWaveProvider(new AudioClip([0.5f], 24000), padding: 3);
        var bytes = new byte[8];

        Assert.Equal(4, provider.Read(bytes, 0, 4));
        Assert.Equal(8, provider.Read(bytes, 0, 8));
        Assert.Equal(4, provider.Read(bytes, 0, 8));
        Assert.Equal(0, provider.Read(bytes, 0, 8));
    }

    [Fact]
    public void Padding_overwrites_whatever_the_buffer_held()
    {
        var provider = new ClipWaveProvider(new AudioClip([0.5f], 24000), padding: 1);
        var bytes = Enumerable.Repeat((byte)0xFF, 8).ToArray();

        provider.Read(bytes, 0, 8);

        Assert.Equal(0f, BitConverter.ToSingle(bytes, 4));
    }

    [Fact]
    public void The_format_is_mono_float_at_the_clips_rate() =>
        Assert.Equal(
            WaveFormat.CreateIeeeFloatWaveFormat(22050, 1),
            new ClipWaveProvider(new AudioClip([0f], 22050), 0).WaveFormat);

    /// <summary>The way NAudio's WaveOut and WasapiOut call it: through a WaveBuffer over the device's bytes.</summary>
    [Fact]
    public void It_reads_into_a_wave_buffer()
    {
        var provider = new ClipWaveProvider(new AudioClip([0.75f, 0.25f], 24000), 0);
        var buffer = new WaveBuffer(16);

        var read = provider.Read(buffer.ByteBuffer, 0, 16);

        Assert.Equal(8, read);
        Assert.Equal(0.75f, buffer.FloatBuffer[0]);
        Assert.Equal(0.25f, buffer.FloatBuffer[1]);
    }
}
