using System.Globalization;
using System.Text.Json;
using MdReader.Core;

namespace MdReader.Tests;

public class AudioAnalyzerTests
{
    private const int Rate = 24000;

    private static AudioClip Sine(float hz, float amplitude, int sampleRate = Rate)
    {
        var samples = new float[sampleRate / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = amplitude * MathF.Sin(2 * MathF.PI * hz * i / sampleRate);
        return new AudioClip(samples, sampleRate);
    }

    private static int Loudest(float[] bands) => Array.IndexOf(bands, bands.Max());

    [Fact]
    public void A_frame_has_16_bands_and_64_wave_points()
    {
        var frame = AudioAnalyzer.Analyze(Sine(1000, 0.5f), 6000);
        Assert.Equal(AudioAnalyzer.BandCount, frame.Bands.Length);
        Assert.Equal(AudioAnalyzer.WavePoints, frame.Wave.Length);
        Assert.Equal((16, 64, 1024), (AudioAnalyzer.BandCount, AudioAnalyzer.WavePoints, AudioAnalyzer.WindowSize));
    }

    [Fact]
    public void Silence_gives_an_all_zero_frame()
    {
        var frame = AudioAnalyzer.Analyze(new AudioClip(new float[Rate], Rate), 6000);
        Assert.Equal(0f, frame.Level);
        Assert.All(frame.Bands, b => Assert.Equal(0f, b));
        Assert.All(frame.Wave, w => Assert.Equal(0f, w));
    }

    [Theory]
    [InlineData(220f)]
    [InlineData(1000f)]
    [InlineData(3000f)]
    public void A_pure_tone_lights_the_band_that_contains_it(float hz)
    {
        var frame = AudioAnalyzer.Analyze(Sine(hz, 0.8f), 6000);
        Assert.Equal(AudioAnalyzer.BandOf(hz), Loudest(frame.Bands));
        Assert.True(frame.Bands[Loudest(frame.Bands)] > 0.85f);
    }

    [Fact]
    public void A_tone_works_at_another_sample_rate()
    {
        var frame = AudioAnalyzer.Analyze(Sine(1000, 0.8f, 22050), 6000);
        Assert.Equal(AudioAnalyzer.BandOf(1000), Loudest(frame.Bands));
    }

    [Theory]
    [InlineData(80f, 0)]
    [InlineData(1000f, 8)]
    [InlineData(7999f, 15)]
    [InlineData(20f, 0)]
    [InlineData(20000f, 15)]
    public void BandOf_maps_a_frequency_to_its_band(float hz, int expected)
    {
        Assert.Equal(expected, AudioAnalyzer.BandOf(hz));
    }

    [Fact]
    public void A_louder_tone_gives_a_higher_level_in_the_speech_range()
    {
        var quiet = AudioAnalyzer.Analyze(Sine(300, 0.05f), 6000).Level;
        var loud = AudioAnalyzer.Analyze(Sine(300, 0.2f), 6000).Level;

        Assert.InRange(quiet, 0.2f, 0.6f);
        Assert.InRange(loud, 0.6f, 0.95f);
        Assert.True(loud > quiet);
    }

    [Fact]
    public void Everything_stays_in_range_for_input_beyond_full_scale()
    {
        var clip = Sine(500, 2.5f);
        var frame = AudioAnalyzer.Analyze(clip, 6000);

        Assert.Equal(1f, frame.Level);
        Assert.All(frame.Bands, b => Assert.InRange(b, 0f, 1f));
        Assert.All(frame.Wave, w => Assert.InRange(w, -1f, 1f));
    }

    [Fact]
    public void Wave_keeps_the_largest_sample_of_each_slice()
    {
        var samples = new float[Rate];
        samples[6000] = -0.9f;
        var frame = AudioAnalyzer.Analyze(new AudioClip(samples, Rate), 6000);

        // The window starts 512 samples before the position, so the impulse is in slice 512 / 16.
        Assert.Equal(-0.9f, frame.Wave[32]);
        Assert.Equal(1, frame.Wave.Count(w => w != 0f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11999)]
    [InlineData(12000)]
    [InlineData(-100000)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Positions_at_and_beyond_the_ends_do_not_throw(int position)
    {
        var frame = AudioAnalyzer.Analyze(Sine(1000, 0.5f), position);
        Assert.InRange(frame.Level, 0f, 1f);
    }

    [Fact]
    public void A_position_far_outside_the_clip_is_silent()
    {
        var frame = AudioAnalyzer.Analyze(Sine(1000, 0.5f), 100000);
        Assert.Equal(0f, frame.Level);
        Assert.All(frame.Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void An_empty_clip_or_a_bad_sample_rate_gives_silence()
    {
        Assert.Same(AudioFrame.Silent, AudioAnalyzer.Analyze(new AudioClip([], Rate), 0));
        Assert.Same(AudioFrame.Silent, AudioAnalyzer.Analyze(new AudioClip(new float[100], 0), 0));
        Assert.Equal((0f, 16, 64), (AudioFrame.Silent.Level, AudioFrame.Silent.Bands.Length, AudioFrame.Silent.Wave.Length));
    }

    [Theory]
    [InlineData(384000L, 384000, 24000, 24000)] // 48 kHz stereo float device, one second
    [InlineData(192000L, 384000, 24000, 12000)]
    [InlineData(96000L, 96000, 24000, 24000)]   // device format equal to the clip's
    [InlineData(88200L, 176400, 22050, 11025)]
    [InlineData(0L, 384000, 24000, 0)]
    [InlineData(5000L, 0, 24000, 0)]
    public void SamplePosition_converts_device_bytes_to_clip_samples(
        long deviceBytes, int bytesPerSecond, int clipRate, int expected)
    {
        Assert.Equal(expected, AudioAnalyzer.SamplePosition(deviceBytes, bytesPerSecond, clipRate));
    }

    [Fact]
    public void ToJson_is_valid_json_whatever_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = AudioAnalyzer.Analyze(Sine(1000, 0.3f), 6000).ToJson();

            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            Assert.InRange(root.GetProperty("level").GetDouble(), 0.01, 1.0);
            Assert.Equal(16, root.GetProperty("bands").GetArrayLength());
            Assert.Equal(64, root.GetProperty("wave").GetArrayLength());
            Assert.DoesNotContain(",,", json);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
