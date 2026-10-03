using System.Diagnostics.CodeAnalysis;
using MdReader.Core;

namespace MdReader.Tests;

public class VisualizerTests
{
    private sealed class FakeProbe : IPlaybackProbe
    {
        public AudioClip? Clip { get; set; }
        public int Position { get; set; }

        public bool TryGetPlayback([NotNullWhen(true)] out AudioClip? clip, out int samplePosition)
        {
            clip = Clip;
            samplePosition = Position;
            return clip is not null;
        }
    }

    private static AudioClip Tone()
    {
        var samples = new float[12000];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 24000f);
        return new AudioClip(samples, 24000);
    }

    [Fact]
    public void Catalog_lists_off_and_the_five_styles_in_menu_order()
    {
        Assert.Equal(
            new[] { "off", "orb", "ring", "bars", "wave", "swarm" },
            VisualizerCatalog.Choices.Select(c => c.Id));
        Assert.Equal(
            new[] { "Off", "Orb", "Ring spectrum", "Bars", "Waveform", "Particle swarm" },
            VisualizerCatalog.Choices.Select(c => c.DisplayName));
        Assert.Equal(("off", "orb"), (VisualizerCatalog.OffId, VisualizerCatalog.DefaultId));
    }

    [Theory]
    [InlineData("swarm", "swarm")]
    [InlineData("off", "off")]
    [InlineData(null, "orb")]
    [InlineData("", "orb")]
    [InlineData("fireworks", "orb")]
    public void Normalize_keeps_known_ids_and_defaults_the_rest(string? id, string expected)
    {
        Assert.Equal(expected, VisualizerCatalog.Normalize(id));
    }

    [Fact]
    public void Feed_sends_nothing_while_nothing_has_played()
    {
        var feed = new VisualizerFeed(new FakeProbe());
        Assert.Null(feed.Next());
        Assert.Null(feed.Next());
    }

    [Fact]
    public void Feed_sends_a_frame_for_each_tick_while_playing()
    {
        var probe = new FakeProbe { Clip = Tone(), Position = 6000 };
        var feed = new VisualizerFeed(probe);

        var first = feed.Next();
        probe.Position = 7000;
        var second = feed.Next();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Level > 0.1f);
        Assert.NotSame(AudioFrame.Silent, second);
    }

    [Fact]
    public void Feed_sends_one_silent_frame_when_playback_stops()
    {
        var probe = new FakeProbe { Clip = Tone(), Position = 6000 };
        var feed = new VisualizerFeed(probe);
        feed.Next();

        probe.Clip = null;
        Assert.Same(AudioFrame.Silent, feed.Next());
        Assert.Null(feed.Next());

        probe.Clip = Tone();
        Assert.NotNull(feed.Next());
    }

    [Fact]
    public void Reset_forgets_that_something_was_playing()
    {
        var probe = new FakeProbe { Clip = Tone(), Position = 6000 };
        var feed = new VisualizerFeed(probe);
        feed.Next();

        probe.Clip = null;
        feed.Reset();
        Assert.Null(feed.Next());
    }
}
