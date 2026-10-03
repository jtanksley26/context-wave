using System.Diagnostics.CodeAnalysis;

namespace MdReader.Core;

public static class VisualizerCatalog
{
    public const string OffId = "off";
    public const string DefaultId = "orb";

    /// <summary>The Visualiser menu, in order.</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> Choices { get; } =
    [
        (OffId, "Off"),
        ("orb", "Orb"),
        ("ring", "Ring spectrum"),
        ("bars", "Bars"),
        ("wave", "Waveform"),
        ("swarm", "Particle swarm"),
    ];

    /// <summary>The id when it is one of the choices, otherwise the default.</summary>
    public static string Normalize(string? id) => Choices.Any(c => c.Id == id) ? id! : DefaultId;
}

/// <summary>Reports the clip being played and how far playback has got.</summary>
public interface IPlaybackProbe
{
    /// <summary>False when nothing is playing or playback is paused.</summary>
    bool TryGetPlayback([NotNullWhen(true)] out AudioClip? clip, out int samplePosition);
}

/// <summary>Decides what to send to the page on each tick of the visualiser timer.</summary>
public sealed class VisualizerFeed(IPlaybackProbe probe)
{
    private bool _resting = true;

    /// <summary>
    /// A frame while something is playing, one <see cref="AudioFrame.Silent"/> when it stops, and
    /// null while there is nothing new to show.
    /// </summary>
    public AudioFrame? Next()
    {
        if (probe.TryGetPlayback(out var clip, out var position))
        {
            _resting = false;
            return AudioAnalyzer.Analyze(clip, position);
        }

        if (_resting) return null;
        _resting = true;
        return AudioFrame.Silent;
    }

    /// <summary>Call when the visualiser is switched off, so nothing stale is sent on the next start.</summary>
    public void Reset() => _resting = true;
}
