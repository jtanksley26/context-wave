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

        // "NaN" is not valid JSON and would make the page reject the whole message.
        static string Number(float value) =>
            float.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "0";
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
