using MdReader.Core;

namespace MdReader.Tests;

public sealed class TimeStretchTests
{
    private const int Rate = 24000;

    private static float[] Tone(double hz, double seconds, float level = 0.5f) =>
        Enumerable.Range(0, (int)(Rate * seconds)).Select(i => level * (float)Math.Sin(2 * Math.PI * hz * i / Rate)).ToArray();

    /// <summary>The tone's pitch from how often it crosses zero, skipping the edges.</summary>
    private static double Pitch(float[] x)
    {
        var from = x.Length / 10;
        var to = x.Length - from;
        var crossings = 0;
        for (var i = from + 1; i < to; i++) if (x[i - 1] < 0 != x[i] < 0) crossings++;
        return crossings / 2.0 / ((to - from) / (double)Rate);
    }

    private static double Rms(float[] x, int from, int to) =>
        Math.Sqrt(x[from..to].Average(v => (double)v * v));

    [Theory]
    [InlineData(1.2)]
    [InlineData(1.6)]
    [InlineData(1.4)]
    public void It_shortens_by_the_factor_and_keeps_the_pitch(double factor)
    {
        var input = Tone(220, 2.0);

        var output = TimeStretch.SpeedUp(input, Rate, factor);

        // Within a crossfade: pieces are whole, so the length lands near, not exactly on, the target.
        Assert.Equal(input.Length / factor, output.Length, Rate * 0.015);
        Assert.Equal(220, Pitch(output), 3.0);
    }

    [Fact]
    public void It_keeps_the_level_without_clipping()
    {
        var output = TimeStretch.SpeedUp(Tone(180, 1.5), Rate, 1.5);

        Assert.Equal(0.5 / Math.Sqrt(2), Rms(output, output.Length / 10, output.Length * 9 / 10), 0.03);
        Assert.True(output.Max(Math.Abs) <= 0.55f);
    }

    [Fact]
    public void The_end_of_the_clip_is_kept()
    {
        // Quiet, then a loud last tenth: speeding up must not drop it.
        var input = Tone(200, 1.0, 0.05f);
        var loud = Tone(200, 0.1, 0.5f);
        Array.Copy(loud, 0, input, input.Length - loud.Length, loud.Length);

        var output = TimeStretch.SpeedUp(input, Rate, 1.6);

        var tail = output.Length - (int)(loud.Length / 1.6) / 2;
        Assert.True(Rms(output, tail, output.Length) > 0.25, "the loud ending was lost");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.8)]
    public void At_or_below_normal_speed_it_returns_the_audio_unchanged(double factor)
    {
        var input = Tone(220, 0.5);
        Assert.Same(input, TimeStretch.SpeedUp(input, Rate, factor));
    }

    [Fact]
    public void A_clip_too_short_to_window_is_returned_unchanged_not_emptied()
    {
        var input = Tone(220, 0.04);
        Assert.Same(input, TimeStretch.SpeedUp(input, Rate, 1.6));
    }
}
