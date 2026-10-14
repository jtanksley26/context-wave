namespace MdReader.Core;

/// <summary>
/// Speeds speech up without changing its pitch. Used for every speed above normal: asked to speak
/// fast, the voice models shorten each sound to a whole number of steps, and the shortest sounds are
/// cut until words slur.
/// </summary>
/// <remarks>
/// SOLA, as SoundTouch does it for speech: long pieces of the input are copied as they are, and each
/// is joined to the one before with a short crossfade, at the offset near its place where the two line
/// up best. Few joins, each between waves in step, keep it smooth; overlapping many short windows
/// instead (WSOLA) measured two to three times the brief dips in loudness, heard as blips.
/// </remarks>
public static class TimeStretch
{
    private const int PieceMs = 80;
    private const int CrossfadeMs = 15;
    private const int SearchMs = 25;

    /// <summary>The audio played <paramref name="factor"/> times faster; unchanged for 1 or less, or a clip too short to piece.</summary>
    public static float[] SpeedUp(float[] input, int sampleRate, double factor)
    {
        var piece = sampleRate * PieceMs / 1000;
        var crossfade = sampleRate * CrossfadeMs / 1000;
        var search = sampleRate * SearchMs / 1000;
        if (factor <= 1.001 || input.Length < piece + crossfade + search) return input;

        var length = (int)Math.Round(input.Length / factor);
        var output = new List<float>(length + piece);
        // Each piece moves the output on by piece - crossfade, and the input by that much times the factor.
        var advance = (piece - crossfade) * factor;
        double place = 0;
        output.AddRange(input.AsSpan(0, piece - crossfade));
        var end = piece; // the end of the last piece copied; its final crossfade samples are not written yet
        while (true)
        {
            place += advance;
            var target = (int)place;
            // Stop while the last join still has room to land the output on its length.
            if (target + piece > input.Length || output.Count + piece > length) break;
            var start = BestMatch(input, end - crossfade, crossfade, target, search, input.Length - piece);
            Join(output, input, end - crossfade, start, crossfade);
            output.AddRange(input.AsSpan(start + crossfade, piece - 2 * crossfade));
            end = start + piece;
        }

        // What is left after the last piece is sped up too, by one last join that ends exactly at the
        // end of the input, so the last sound is whole and the sentence does not slow down at the end.
        // Sized by the output still owed: the input read so far runs behind where a perfectly even
        // stretch would be, since each piece is taken ahead of where it is written.
        var owed = length - output.Count;
        var last = input.Length - owed;
        if (owed >= crossfade && last > end - crossfade)
        {
            // A narrow search either side, so the length stays within a crossfade of its target.
            var start = BestMatch(input, end - crossfade, crossfade, last, Math.Min(crossfade, owed - crossfade), input.Length - crossfade);
            Join(output, input, end - crossfade, start, crossfade);
            output.AddRange(input.AsSpan(start + crossfade));
        }
        else
        {
            output.AddRange(input.AsSpan(end - crossfade));
        }
        return [.. output];
    }

    /// <summary>Crossfades from the input at <paramref name="from"/> into the input at <paramref name="to"/>.</summary>
    private static void Join(List<float> output, float[] input, int from, int to, int length)
    {
        for (var i = 0; i < length; i++)
        {
            var t = (i + 0.5f) / length;
            output.Add(input[from + i] * (1 - t) + input[to + i] * t);
        }
    }

    /// <summary>
    /// The start near <paramref name="target"/>, at most <paramref name="latest"/>, whose opening best
    /// matches the input at <paramref name="tail"/>.
    /// </summary>
    private static int BestMatch(float[] input, int tail, int length, int target, int search, int latest)
    {
        var best = Math.Min(target, latest);
        var bestScore = double.MinValue;
        for (var start = Math.Max(0, target - search); start <= Math.Min(latest, target + search); start++)
        {
            double dot = 0, energy = 1e-9;
            for (var i = 0; i < length; i++)
            {
                dot += input[start + i] * input[tail + i];
                energy += input[start + i] * input[start + i];
            }
            var score = dot / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = start;
            }
        }
        return best;
    }
}
