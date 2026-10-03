# Voice Visualiser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a panel above the text that animates with the reader's voice, in one of five styles (Orb, Ring spectrum, Bars, Waveform, Particle swarm) chosen from Settings.

**Architecture:** A UI-free `AudioAnalyzer` in `MdReader.Core` turns a clip and a playback position into an `AudioFrame` (loudness, 16 pitch bands, 64 wave points). `NAudioOutput` reports the clip being played and how far it has got. About 30 times a second a timer in the window asks a small `VisualizerFeed` for the next frame and posts it to the WebView page, where a canvas draws the chosen style and animates between frames.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF, WebView2, NAudio, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-visualizer-design.md`

## Notes for the implementer

- **Output folder.** Every dotnet command in this plan passes `-p:MdReaderOut=../../out-dev/`. Never build without it: `out/` is held open by the running app and by Claude sessions.
- Run commands from the repository root (`D:\Projects\md5reader`). They are written for Git Bash and work unchanged in PowerShell.
- Do not launch the app from a task. A second instance only activates the running one and exits.
- The page's stylesheet and script live in a C# raw string literal (`ShellHtml` in `src/MdReader.App/DocumentView.cs`). Keep the surrounding indentation when editing it, and never put three double quotes in a row inside it.
- Deviations from the spec, recorded in its "Amendments" section:
  - There is no `VisualizerPump` class. The decision of what to send each tick is `VisualizerFeed` in Core (so it is unit tested), driven by a `DispatcherTimer` in `MainWindow`.
  - `NAudioOutput` implements a new Core interface, `IPlaybackProbe`, which is what `VisualizerFeed` depends on.
  - `AudioAnalyzer.Analyze` allocates its work buffers on each call (about 12 KB, 30 times a second) instead of reusing them; it keeps the code free of shared state.
  - `AudioFrame.ToJson()` builds the message for the page, and `AudioAnalyzer.BandOf(hz)` says which band a frequency falls in.

## File map

```
src/MdReader.Core/
  AudioAnalyzer.cs       (new)   AudioFrame, AudioAnalyzer
  Visualizer.cs          (new)   VisualizerCatalog, IPlaybackProbe, VisualizerFeed
  Settings.cs                    Visualizer value
src/MdReader.App/
  NAudioOutput.cs                TryGetPlayback
  DocumentView.cs                panel, canvas, five styles, SetVisualizer, PushAudio
  MainWindow.xaml                Visualiser submenu
  MainWindow.xaml.cs             menu, timer
tests/MdReader.Tests/
  AudioAnalyzerTests.cs  (new)
  VisualizerTests.cs     (new)
  SettingsTests.cs               one new test
README.md
```

---

### Task 1: Audio analysis

**Files:**
- Create: `src/MdReader.Core/AudioAnalyzer.cs`
- Create: `tests/MdReader.Tests/AudioAnalyzerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/AudioAnalyzerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~AudioAnalyzerTests"`
Expected: build fails with `error CS0103: The name 'AudioAnalyzer' does not exist in the current context`.

- [ ] **Step 3: Write the analyzer**

Create `src/MdReader.Core/AudioAnalyzer.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace MdReader.Core;

/// <summary>What the voice sounds like at one instant.</summary>
/// <param name="Level">Loudness, 0 to 1.</param>
/// <param name="Bands">Pitch bands from low to high, each 0 to 1.</param>
/// <param name="Wave">The shape of the sound, each point -1 to 1.</param>
public sealed record AudioFrame(float Level, float[] Bands, float[] Wave)
{
    public static AudioFrame Silent { get; } =
        new(0f, new float[AudioAnalyzer.BandCount], new float[AudioAnalyzer.WavePoints]);

    /// <summary>The message for the page: {"level":…,"bands":[…],"wave":[…]}.</summary>
    public string ToJson()
    {
        var json = new StringBuilder(640);
        json.Append("{\"level\":").Append(Number(Level)).Append(",\"bands\":[");
        AppendAll(json, Bands);
        json.Append("],\"wave\":[");
        AppendAll(json, Wave);
        return json.Append("]}").ToString();

        static void AppendAll(StringBuilder target, float[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) target.Append(',');
                target.Append(Number(values[i]));
            }
        }

        static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public static class AudioAnalyzer
{
    public const int WindowSize = 1024;
    public const int BandCount = 16;
    public const int WavePoints = 64;

    // The bands cover the range of speech.
    private const float LowHz = 80f;
    private const float HighHz = 8000f;
    private const float FloorDb = -60f;

    // A root-mean-square of 0.25 or more counts as full level; ordinary speech is 0.05 to 0.2.
    private const float FullLevelRms = 0.25f;

    private static readonly float[] Hann = CreateHann();

    /// <summary>
    /// Analyses the <see cref="WindowSize"/> samples centred on <paramref name="samplePosition"/>.
    /// Samples outside the clip count as silence, so any position is valid.
    /// </summary>
    public static AudioFrame Analyze(AudioClip clip, int samplePosition)
    {
        if (clip.Samples.Length == 0 || clip.SampleRate <= 0) return AudioFrame.Silent;

        var window = new float[WindowSize];
        var start = (long)samplePosition - WindowSize / 2;
        double sumOfSquares = 0;
        for (var i = 0; i < WindowSize; i++)
        {
            var index = start + i;
            var sample = index >= 0 && index < clip.Samples.Length ? clip.Samples[index] : 0f;
            window[i] = sample;
            sumOfSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumOfSquares / WindowSize);
        var level = (float)Math.Min(1.0, Math.Sqrt(rms / FullLevelRms));
        return new AudioFrame(level, Bands(window, clip.SampleRate), Wave(window));
    }

    /// <summary>The band a frequency falls in; frequencies outside the range go to the end bands.</summary>
    public static int BandOf(float hz)
    {
        var band = (int)MathF.Floor(BandCount * MathF.Log(hz / LowHz) / MathF.Log(HighHz / LowHz));
        return Math.Clamp(band, 0, BandCount - 1);
    }

    /// <summary>Converts the audio device's byte position to a sample index in the clip being played.</summary>
    public static int SamplePosition(long deviceBytes, int deviceBytesPerSecond, int clipSampleRate) =>
        deviceBytesPerSecond <= 0 ? 0 : (int)(deviceBytes * clipSampleRate / deviceBytesPerSecond);

    private static float BandEdge(int band) => LowHz * MathF.Pow(HighHz / LowHz, (float)band / BandCount);

    private static float[] Bands(float[] window, int sampleRate)
    {
        var real = new float[WindowSize];
        var imaginary = new float[WindowSize];
        for (var i = 0; i < WindowSize; i++) real[i] = window[i] * Hann[i];
        Fft(real, imaginary);

        var bands = new float[BandCount];
        var binHz = (float)sampleRate / WindowSize;
        const int lastBin = WindowSize / 2 - 1;
        for (var band = 0; band < BandCount; band++)
        {
            var low = BandEdge(band);
            var high = BandEdge(band + 1);
            // The bins whose frequency is in [low, high).
            var first = (int)MathF.Ceiling(low / binHz);
            var last = (int)MathF.Ceiling(high / binHz) - 1;
            // A band narrower than one bin takes the bin nearest its centre.
            if (last < first) first = last = (int)MathF.Round(MathF.Sqrt(low * high) / binHz);
            first = Math.Max(first, 1);
            if (first > lastBin) continue;
            last = Math.Min(last, lastBin);

            var peak = 0f;
            for (var bin = first; bin <= last; bin++)
            {
                var magnitude = MathF.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
                if (magnitude > peak) peak = magnitude;
            }

            // A full-scale sine through a Hann window peaks at WindowSize / 4, which is 0 dB here.
            var decibels = 20f * MathF.Log10(peak / (WindowSize / 4f) + 1e-9f);
            bands[band] = Math.Clamp((decibels - FloorDb) / -FloorDb, 0f, 1f);
        }
        return bands;
    }

    private static float[] Wave(float[] window)
    {
        var wave = new float[WavePoints];
        const int slice = WindowSize / WavePoints;
        for (var point = 0; point < WavePoints; point++)
        {
            var largest = 0f;
            for (var i = point * slice; i < (point + 1) * slice; i++)
                if (MathF.Abs(window[i]) > MathF.Abs(largest)) largest = window[i];
            wave[point] = Math.Clamp(largest, -1f, 1f);
        }
        return wave;
    }

    private static float[] CreateHann()
    {
        var hann = new float[WindowSize];
        for (var i = 0; i < WindowSize; i++)
            hann[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (WindowSize - 1));
        return hann;
    }

    /// <summary>In-place radix-2 FFT; the length must be a power of two.</summary>
    private static void Fft(float[] real, float[] imaginary)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var stepReal = (float)Math.Cos(angle);
            var stepImaginary = (float)Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                float turnReal = 1, turnImaginary = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var a = i + k;
                    var b = a + length / 2;
                    var oddReal = real[b] * turnReal - imaginary[b] * turnImaginary;
                    var oddImaginary = real[b] * turnImaginary + imaginary[b] * turnReal;
                    real[b] = real[a] - oddReal;
                    imaginary[b] = imaginary[a] - oddImaginary;
                    real[a] += oddReal;
                    imaginary[a] += oddImaginary;

                    var nextReal = turnReal * stepReal - turnImaginary * stepImaginary;
                    turnImaginary = turnReal * stepImaginary + turnImaginary * stepReal;
                    turnReal = nextReal;
                }
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~AudioAnalyzerTests"`
Expected: PASS, 30 test cases.

If a tone test fails, print the 16 band values before changing anything: the analysis maths is the thing under test, and a wrong band usually means an error in the FFT or the band edges, not in the test.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/AudioAnalyzer.cs tests/MdReader.Tests/AudioAnalyzerTests.cs
git commit -m "feat(core): analyse a clip into level, pitch bands and wave shape"
```

---

### Task 2: Catalog, feed and setting

**Files:**
- Create: `src/MdReader.Core/Visualizer.cs`
- Modify: `src/MdReader.Core/Settings.cs`
- Create: `tests/MdReader.Tests/VisualizerTests.cs`
- Modify: `tests/MdReader.Tests/SettingsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/VisualizerTests.cs`:

```csharp
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
```

Add to `SettingsTests`, before the closing brace of the class:

```csharp
    [Fact]
    public void Visualizer_defaults_round_trips_and_falls_back_when_blank()
    {
        Assert.Equal("orb", Settings.Load(File_).Visualizer);

        new Settings { Visualizer = "swarm" }.Save(File_);
        Assert.Equal("swarm", Settings.Load(File_).Visualizer);

        File.WriteAllText(File_, "{\"Visualizer\":null}");
        Assert.Equal("orb", Settings.Load(File_).Visualizer);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~VisualizerTests|FullyQualifiedName~SettingsTests"`
Expected: build fails with `error CS0103: The name 'VisualizerCatalog' does not exist in the current context`.

- [ ] **Step 3: Write the catalog and the feed**

Create `src/MdReader.Core/Visualizer.cs`:

```csharp
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
```

- [ ] **Step 4: Add the setting**

In `src/MdReader.Core/Settings.cs` add a property after `Highlight`:

```csharp
    public string Visualizer { get; set; } = VisualizerCatalog.DefaultId;
```

In `Load`, after the line that defaults `loaded.Highlight`, add:

```csharp
                if (string.IsNullOrEmpty(loaded.Visualizer)) loaded.Visualizer = VisualizerCatalog.DefaultId;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~VisualizerTests|FullyQualifiedName~SettingsTests"`
Expected: PASS, 19 test cases (10 in VisualizerTests, 9 in SettingsTests).

- [ ] **Step 6: Commit**

```bash
git add src/MdReader.Core/Visualizer.cs src/MdReader.Core/Settings.cs tests/MdReader.Tests/VisualizerTests.cs tests/MdReader.Tests/SettingsTests.cs
git commit -m "feat(core): add the visualiser catalog, feed and setting"
```

---

### Task 3: Playback position

**Files:**
- Modify: `src/MdReader.App/NAudioOutput.cs`

The conversion is tested in Task 1 (`SamplePosition_converts_device_bytes_to_clip_samples`). Reading the device needs a real audio device, so this task is verified by the build and by the manual check at the end.

- [ ] **Step 1: Report the clip and position**

In `src/MdReader.App/NAudioOutput.cs`:

Add two usings at the top:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
```

Change the class declaration to implement the probe:

```csharp
public sealed class NAudioOutput : IAudioOutput, IPlaybackProbe, IDisposable
```

Add below `private WasapiOut? _current;`:

```csharp
    private AudioClip? _clip;

    /// <summary>
    /// Added to the reported position, to line the visualiser up with what is heard. Positive
    /// shows the sound earlier, negative later.
    /// </summary>
    private const int LatencyOffsetMs = 0;
```

In `PlayAsync`, replace

```csharp
        lock (_lock) _current = device;
```

with

```csharp
        lock (_lock)
        {
            _current = device;
            _clip = clip;
        }
```

and replace

```csharp
            lock (_lock) _current = null;
```

with

```csharp
            lock (_lock)
            {
                _current = null;
                _clip = null;
            }
```

Add this method above `public void Pause()`:

```csharp
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
```

- [ ] **Step 2: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors and 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/MdReader.App/NAudioOutput.cs
git commit -m "feat(app): report the playing clip and its position"
```

---

### Task 4: Visualiser panel in the page

**Files:**
- Modify: `src/MdReader.App/DocumentView.cs`

Verified by the build here and by the browser check in Task 6.

- [ ] **Step 1: Add the panel's styles**

In the `ShellHtml` constant, add these rules directly above the line `/* Without a diff the page is the single centred column above. */`:

```css
          /* The visualiser sits flush at the top of the text pane and stays there while text scrolls. */
          #viz { position:sticky; top:0; z-index:2; height:140px; margin:-24px -32px 16px;
                 background:var(--bg); border-bottom:1px solid var(--line); pointer-events:none; }
          #viz.off { display:none; }
          #viz canvas { display:block; width:100%; height:100%; }

```

- [ ] **Step 2: Add the panel's markup**

Replace

```html
        <div id="text">
          <div id="doc"></div>
```

with

```html
        <div id="text">
          <div id="viz" class="off"><canvas></canvas></div>
          <div id="doc"></div>
```

- [ ] **Step 3: Repaint on a theme change**

In the page's `setTheme` function, add a last line inside the function body, after the line that sets `root.style.colorScheme`:

```js
            wake();
```

- [ ] **Step 4: Add the visualiser script**

Add this block at the end of the `<script>` element, directly above the closing `</script>` tag:

```js

          // ---- Voice visualiser ----
          const viz = document.getElementById('viz');
          const canvas = viz.querySelector('canvas');
          const ctx = canvas.getContext('2d');
          const BANDS = 16, WAVE = 64, REST_MS = 3000, TAU = Math.PI * 2;
          const zeros = n => new Array(n).fill(0);
          const vz = {
            style: 'off',
            target: { level: 0, bands: zeros(BANDS), wave: zeros(WAVE) },
            level: 0, bands: zeros(BANDS), wave: zeros(WAVE), peaks: zeros(BANDS),
            rings: [], dots: [], trend: 0, awakeUntil: 0, raf: 0, last: 0, w: 0, h: 0
          };

          function setVisualizer(style) {
            vz.style = style;
            viz.classList.toggle('off', style === 'off');
            vz.rings = [];
            if (style === 'off') {
              cancelAnimationFrame(vz.raf);
              vz.raf = 0;
              return;
            }
            wake();
          }

          // frame is { level, bands[16], wave[64] } from the app, about 30 times a second.
          function pushAudio(frame) {
            if (vz.style === 'off') return;
            vz.target = frame;
            if (frame.level > 0.02) vz.awakeUntil = performance.now() + REST_MS;
            wake();
          }

          // Runs the loop for at least a moment, so a change is painted even at rest.
          function wake() {
            if (vz.style === 'off') return;
            const now = performance.now();
            vz.awakeUntil = Math.max(vz.awakeUntil, now + 600);
            if (!vz.raf) {
              vz.last = now;
              vz.raf = requestAnimationFrame(tick);
            }
          }

          function sizeCanvas() {
            const box = viz.getBoundingClientRect();
            const ratio = window.devicePixelRatio || 1;
            vz.w = box.width;
            vz.h = box.height;
            const width = Math.round(box.width * ratio), height = Math.round(box.height * ratio);
            if (canvas.width !== width || canvas.height !== height) {
              canvas.width = width;
              canvas.height = height;
            }
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
          }

          // Moves current towards target; quick on the way up, slower on the way down.
          function ease(current, target, dt, up, down) {
            return current + (target - current) * (1 - Math.exp(-dt * (target > current ? up : down)));
          }

          function tick(now) {
            vz.raf = 0;
            if (vz.style === 'off') return;
            const dt = Math.min(0.05, (now - vz.last) / 1000);
            vz.last = now;
            sizeCanvas();

            if (vz.w > 0 && vz.h > 0) {
              const t = vz.target;
              vz.level = ease(vz.level, t.level || 0, dt, 30, 6);
              for (let i = 0; i < BANDS; i++) {
                vz.bands[i] = ease(vz.bands[i], t.bands[i] || 0, dt, 35, 8);
                vz.peaks[i] = Math.max(vz.bands[i], vz.peaks[i] - dt * 0.35);
              }
              for (let i = 0; i < WAVE; i++) vz.wave[i] = ease(vz.wave[i], t.wave[i] || 0, dt, 40, 40);

              const css = getComputedStyle(document.documentElement);
              const look = {
                main: css.getPropertyValue('--focus').trim(),
                dim: css.getPropertyValue('--fg').trim(),
                glow: document.documentElement.style.colorScheme === 'dark' ? 14 : 0
              };
              // At rest the level never quite reaches zero, which gives the faint slow pulse.
              const rest = 0.05 + 0.03 * Math.sin(now / 900);
              const level = Math.max(vz.level, rest);

              ctx.clearRect(0, 0, vz.w, vz.h);
              ctx.globalAlpha = 1;
              ctx.lineCap = 'round';
              ctx.shadowColor = look.main;
              ctx.shadowBlur = look.glow;
              (styles[vz.style] || styles.orb)(vz.w, vz.h, level, dt, now, look);
              ctx.globalAlpha = 1;
              ctx.shadowBlur = 0;
            }

            if (now < vz.awakeUntil) vz.raf = requestAnimationFrame(tick);
          }

          const styles = {
            orb(w, h, level, dt, now, look) {
              const cx = w / 2, cy = h / 2, base = h * 0.16;
              // A ring leaves the core each time the level jumps.
              if (level - vz.trend > 0.12 && vz.rings.length < 6) vz.rings.push({ r: base * (1 + level), a: 0.7 });
              vz.trend = ease(vz.trend, level, dt, 8, 8);

              ctx.lineWidth = 1.5;
              ctx.strokeStyle = look.main;
              for (const ring of vz.rings) {
                ring.r += dt * h * 0.9;
                ring.a -= dt * 0.9;
                ctx.globalAlpha = Math.max(0, ring.a);
                ctx.beginPath();
                ctx.arc(cx, cy, ring.r, 0, TAU);
                ctx.stroke();
              }
              vz.rings = vz.rings.filter(ring => ring.a > 0);

              ctx.strokeStyle = look.dim;
              ctx.globalAlpha = 0.22;
              ctx.shadowBlur = 0;
              for (const k of [0.36, 0.45]) {
                ctx.beginPath();
                ctx.arc(cx, cy, h * k, 0, TAU);
                ctx.stroke();
              }

              ctx.shadowBlur = look.glow;
              ctx.fillStyle = look.main;
              const radius = base * (0.8 + level * 1.5);
              for (const [scale, alpha] of [[1.5, 0.18], [1.2, 0.4], [0.85, 1]]) {
                ctx.globalAlpha = alpha;
                ctx.beginPath();
                ctx.arc(cx, cy, radius * scale, 0, TAU);
                ctx.fill();
              }
            },

            ring(w, h, level, dt, now, look) {
              const cx = w / 2, cy = h / 2, inner = h * 0.2, reach = h * 0.26;
              ctx.strokeStyle = look.dim;
              ctx.globalAlpha = 0.35;
              ctx.lineWidth = 1;
              ctx.shadowBlur = 0;
              ctx.beginPath();
              ctx.arc(cx, cy, inner - 4, 0, TAU);
              ctx.stroke();

              ctx.strokeStyle = look.main;
              ctx.globalAlpha = 1;
              ctx.lineWidth = 3;
              ctx.shadowBlur = look.glow;
              ctx.beginPath();
              for (let i = 0; i < BANDS; i++) {
                const length = 3 + Math.max(vz.bands[i], level * 0.15) * reach;
                // Low bands at the top, high at the bottom, mirrored on the left.
                const angle = -Math.PI / 2 + (i + 0.5) / BANDS * Math.PI;
                for (const a of [angle, Math.PI - angle]) {
                  const x = Math.cos(a), y = Math.sin(a);
                  ctx.moveTo(cx + x * inner, cy + y * inner);
                  ctx.lineTo(cx + x * (inner + length), cy + y * (inner + length));
                }
              }
              ctx.stroke();
            },

            bars(w, h, level, dt, now, look) {
              const gap = 5, total = Math.min(w - 40, 520), bar = (total - gap * (BANDS - 1)) / BANDS;
              const left = (w - total) / 2, floor = h - 14, tall = h - 30;
              ctx.fillStyle = look.main;
              for (let i = 0; i < BANDS; i++) {
                const height = Math.max(2, Math.max(vz.bands[i], level * 0.08) * tall);
                ctx.globalAlpha = 0.9;
                ctx.fillRect(left + i * (bar + gap), floor - height, bar, height);
              }
              ctx.fillStyle = look.dim;
              ctx.globalAlpha = 0.5;
              ctx.shadowBlur = 0;
              for (let i = 0; i < BANDS; i++) {
                ctx.fillRect(left + i * (bar + gap), floor - Math.max(4, vz.peaks[i] * tall) - 4, bar, 2);
              }
            },

            wave(w, h, level, dt, now, look) {
              const pad = 16, mid = h / 2, reach = h * 0.4;
              const trace = sign => {
                ctx.beginPath();
                for (let i = 0; i < WAVE; i++) {
                  const x = pad + i / (WAVE - 1) * (w - pad * 2);
                  // A slow ripple keeps the line alive at rest.
                  const y = mid - sign * (vz.wave[i] * reach + Math.sin(i * 0.45 + now / 450) * 1.5);
                  if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
                }
                ctx.stroke();
              };
              ctx.lineJoin = 'round';
              ctx.strokeStyle = look.dim;
              ctx.globalAlpha = 0.25;
              ctx.lineWidth = 1.5;
              ctx.shadowBlur = 0;
              trace(-1);
              ctx.strokeStyle = look.main;
              ctx.globalAlpha = 1;
              ctx.lineWidth = 2;
              ctx.shadowBlur = look.glow;
              trace(1);
            },

            swarm(w, h, level, dt, now, look) {
              if (vz.dots.length === 0) {
                for (let i = 0; i < 140; i++) {
                  vz.dots.push({
                    angle: Math.random() * TAU,
                    orbit: 0.25 + Math.random() * 0.75,
                    speed: (0.25 + Math.random() * 0.9) * (Math.random() < 0.5 ? -1 : 1),
                    band: Math.floor(Math.random() * BANDS),
                    radius: 0.3
                  });
                }
              }
              const cx = w / 2, cy = h / 2, rx = Math.min(w * 0.42, h * 1.7), ry = h * 0.42;
              ctx.fillStyle = look.main;
              ctx.globalAlpha = 0.35 + 0.6 * Math.min(1, level * 1.4);
              ctx.beginPath();
              for (const dot of vz.dots) {
                const band = vz.bands[dot.band];
                dot.angle += dt * dot.speed * (0.4 + level * 1.8);
                // Loud syllables fling the dots out; in quiet they drift back towards the centre.
                dot.radius = ease(dot.radius, dot.orbit * (0.3 + level * 0.75) + band * 0.2, dt, 10, 3);
                const x = cx + Math.cos(dot.angle) * dot.radius * rx;
                const y = cy + Math.sin(dot.angle) * dot.radius * ry;
                const size = 1 + band * 2.2;
                ctx.moveTo(x + size, y);
                ctx.arc(x, y, size, 0, TAU);
              }
              ctx.fill();
            }
          };

          window.addEventListener('resize', wake);
          if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
            window.chrome.webview.addEventListener('message', e => pushAudio(e.data));
          }
```

- [ ] **Step 5: Add the C# side**

Below the field `private ResolvedTheme? _theme;` add:

```csharp
    private string _visualizer = VisualizerCatalog.OffId;
```

In `InitializeAsync`, directly after the line `if (_theme is not null) SetTheme(_theme);`, add:

```csharp
        SetVisualizer(_visualizer);
```

Above `public void SetDiff(string html, string? title) =>` add:

```csharp
    /// <summary>Shows the visualiser in this style, or hides it for "off". Applies once the page has loaded.</summary>
    public void SetVisualizer(string id)
    {
        _visualizer = id;
        Run($"setVisualizer({JsonSerializer.Serialize(id)})");
    }

    /// <summary>Sends one instant of sound to the visualiser.</summary>
    public void PushAudio(AudioFrame frame)
    {
        if (_loaded) webView.CoreWebView2.PostWebMessageAsJson(frame.ToJson());
    }
```

- [ ] **Step 6: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors and 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/MdReader.App/DocumentView.cs
git commit -m "feat(app): draw the voice visualiser in the page"
```

---

### Task 5: Menu and timer

**Files:**
- Modify: `src/MdReader.App/MainWindow.xaml`
- Modify: `src/MdReader.App/MainWindow.xaml.cs`

- [ ] **Step 1: Add the submenu**

In `src/MdReader.App/MainWindow.xaml`, directly below the line

```xml
                <MenuItem x:Name="HighlightMenu" Header="_Highlight colour" />
```

add

```xml
                <MenuItem x:Name="VisualizerMenu" Header="_Visualiser" />
```

- [ ] **Step 2: Wire the menu and the timer**

In `src/MdReader.App/MainWindow.xaml.cs`:

Add a using at the top, with the other `System.Windows` usings:

```csharp
using System.Windows.Threading;
```

Add fields below `private readonly PipeServer _pipe;`:

```csharp
    private readonly VisualizerFeed _feed;

    // About 30 updates a second; the page animates smoothly between them.
    private readonly DispatcherTimer _visualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
```

In the constructor, directly below the line `ApplyTheme();`, add:

```csharp
        _feed = new VisualizerFeed(_output);
        _visualizerTimer.Tick += (_, _) =>
        {
            if (_feed.Next() is { } frame) _view.PushAudio(frame);
        };
        BuildVisualizerMenu();
        ApplyVisualizer();
```

In `OnClosed`, add as the first line of the method:

```csharp
        _visualizerTimer.Stop();
```

Add these methods directly above `private void OnThemeClick(object sender, RoutedEventArgs e)`:

```csharp
    private void BuildVisualizerMenu()
    {
        foreach (var (id, name) in VisualizerCatalog.Choices)
        {
            var item = new MenuItem { Header = name, Tag = id, IsCheckable = true };
            item.Click += OnVisualizerClick;
            VisualizerMenu.Items.Add(item);
        }
    }

    /// <summary>Shows the chosen style and runs the timer only while the visualiser is on.</summary>
    private void ApplyVisualizer()
    {
        var id = VisualizerCatalog.Normalize(_settings.Visualizer);
        foreach (MenuItem item in VisualizerMenu.Items) item.IsChecked = (string)item.Tag == id;
        _view.SetVisualizer(id);

        if (id == VisualizerCatalog.OffId)
        {
            _visualizerTimer.Stop();
            _feed.Reset();
        }
        else
        {
            _visualizerTimer.Start();
        }
    }

    private void OnVisualizerClick(object sender, RoutedEventArgs e)
    {
        _settings.Visualizer = (string)((MenuItem)sender).Tag;
        SaveSettings();
        ApplyVisualizer();
    }
```

- [ ] **Step 3: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors and 0 warnings.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.App/MainWindow.xaml src/MdReader.App/MainWindow.xaml.cs
git commit -m "feat(app): choose the visualiser style from the Settings menu"
```

---

### Task 6: Page check, documentation and hand-over

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Check the page in a browser**

This step is done by the controller, not a subagent, because it needs the browser tools.

Extract `ShellHtml` into a scratch `index.html` outside the repository, add a stub for `window.chrome.webview.postMessage`, and serve it on `127.0.0.1`. Produce a sequence of real frames by running `AudioAnalyzer.Analyze` over a speech-like signal with the built `out-dev/MdReader.Core.dll` (30 frames a second for about three seconds, followed by silent frames) and embed it in the page.

For each of the five styles, in one light and one dark theme (set with `setTheme`):
- call `setVisualizer(style)` and feed the frames with `pushAudio` at 33 ms intervals;
- confirm from canvas pixel data that the canvas is not blank and that it changes between two moments during speech;
- take a screenshot mid-speech and look at it;
- after the silent frames, confirm the loop stops (`vz.raf` is 0 a few seconds later).

Also confirm: `setVisualizer('off')` hides the panel and `pushAudio` then does nothing; the panel stays at the top while the text pane scrolls, in both the single-column and the split (diff) layout; a sentence highlight still scrolls into view below the panel; there are no console errors.

Fix anything found in `DocumentView.cs`, rebuild, and commit the fix with a `fix(app):` message.

- [ ] **Step 2: Update the README**

In `README.md`, add to the end of the "Appearance" section:

```markdown

**Settings > Visualiser** shows a panel above the text that moves with the voice: Orb, Ring
spectrum, Bars, Waveform or Particle swarm. Off hides it and stops the animation.
```

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: describe the voice visualiser"
```

- [ ] **Step 5: Hand over for the real build and the listening check**

Whether the motion is in time with the voice can only be judged by ear. After the user agrees to close MD Reader, rebuild into `out/`, start it, and have it read something. Ask the user to check:

- The Orb panel appears above the text and moves while it reads, and rests when paused or finished.
- Each style under **Settings > Visualiser** draws and moves; **Off** removes the panel.
- The motion looks in time with the voice. If it runs ahead of or behind the sound, ask which way and by roughly how much, then adjust `LatencyOffsetMs` in `NAudioOutput.cs`.
- Pause, Next, Previous, a speed change and clicking a sentence all leave the visualiser behaving sensibly.
- It follows the theme and the highlight colour.
- During a diff walkthrough the panel is above the text pane only.
