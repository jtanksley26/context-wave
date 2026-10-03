using System.Text.RegularExpressions;

namespace MdReader.Core;

public enum DiffFileKind { Modified, Added, Deleted, Renamed, Binary }

public enum DiffLineKind { Context, Added, Removed, HunkHeader }

/// <param name="Index">Position within the whole diff; used as the element id in the page.</param>
public sealed record DiffLine(int Index, DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

public sealed record DiffFile(
    int Index, string OldPath, string NewPath, DiffFileKind Kind, IReadOnlyList<DiffLine> Lines)
{
    public string DisplayPath => Kind == DiffFileKind.Deleted ? OldPath : NewPath;
}

public sealed partial class DiffDocument
{
    private sealed class FileBuilder(bool fromGit)
    {
        public bool FromGit { get; } = fromGit;
        public string OldPath = "";
        public string NewPath = "";
        public DiffFileKind Kind = DiffFileKind.Modified;
        public bool SawOldHeader;
        public List<DiffLine> Lines { get; } = [];
    }

    [GeneratedRegex(@"^diff --git a/(.+?) b/(.+)$")]
    private static partial Regex GitHeader();

    [GeneratedRegex(@"^@@ -(\d{1,9})(?:,(\d{1,9}))? \+(\d{1,9})(?:,(\d{1,9}))? @@")]
    private static partial Regex HunkHeader();

    private DiffDocument(IReadOnlyList<DiffFile> files) => Files = files;

    public IReadOnlyList<DiffFile> Files { get; }

    /// <exception cref="ReaderException">The text is not a unified diff.</exception>
    public static DiffDocument Parse(string unifiedDiff)
    {
        var builders = new List<FileBuilder>();
        FileBuilder? current = null;
        var nextLine = 0;
        int oldNumber = 0, newNumber = 0, oldLeft = 0, newLeft = 0;

        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith('\\')) continue; // "\ No newline at end of file"

            // The counts from the hunk header decide what is a hunk line, so removed or added text
            // that happens to look like a header ("--- x") stays in its hunk.
            if (current is not null && (oldLeft > 0 || newLeft > 0))
            {
                var marker = line.Length == 0 ? ' ' : line[0];
                var text = line.Length == 0 ? "" : line[1..];
                if (marker == '+' && newLeft > 0)
                {
                    current.Lines.Add(new DiffLine(nextLine++, DiffLineKind.Added, null, newNumber++, text));
                    newLeft--;
                    continue;
                }
                if (marker == '-' && oldLeft > 0)
                {
                    current.Lines.Add(new DiffLine(nextLine++, DiffLineKind.Removed, oldNumber++, null, text));
                    oldLeft--;
                    continue;
                }
                if (marker == ' ' && oldLeft > 0 && newLeft > 0)
                {
                    current.Lines.Add(
                        new DiffLine(nextLine++, DiffLineKind.Context, oldNumber++, newNumber++, text));
                    oldLeft--;
                    newLeft--;
                    continue;
                }
                // The hunk is shorter than its header claimed; treat this line as a header line.
                oldLeft = newLeft = 0;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = new FileBuilder(fromGit: true);
                builders.Add(current);
                var rest = line["diff --git ".Length..];
                var git = GitHeader().Match(line);
                if (rest.Contains('"'))
                {
                    if (SplitQuotedPair(rest) is var (first, second))
                    {
                        first = Unquote(first);
                        second = Unquote(second);
                        current.OldPath = first.StartsWith("a/", StringComparison.Ordinal) ? first[2..] : first;
                        current.NewPath = second.StartsWith("b/", StringComparison.Ordinal) ? second[2..] : second;
                    }
                }
                else if (git.Success)
                {
                    current.OldPath = git.Groups[1].Value;
                    current.NewPath = git.Groups[2].Value;
                }
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (current is null || current.SawOldHeader)
                {
                    current = new FileBuilder(fromGit: false);
                    builders.Add(current);
                }
                current.SawOldHeader = true;
                if (HeaderPath(line[4..]) is { } path) current.OldPath = path;
                else current.Kind = DiffFileKind.Added;
            }
            else if (current is null)
            {
                // Text before the first file, such as a commit message.
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (HeaderPath(line[4..]) is { } path) current.NewPath = path;
                else current.Kind = DiffFileKind.Deleted;
            }
            else if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                current.Kind = DiffFileKind.Added;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                current.Kind = DiffFileKind.Deleted;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                current.OldPath = Unquote(line["rename from ".Length..]);
                current.Kind = DiffFileKind.Renamed;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                current.NewPath = Unquote(line["rename to ".Length..]);
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                current.Kind = DiffFileKind.Binary;
            }
            else if (HunkHeader().Match(line) is { Success: true } hunk)
            {
                oldNumber = int.Parse(hunk.Groups[1].Value);
                oldLeft = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value) : 1;
                newNumber = int.Parse(hunk.Groups[3].Value);
                newLeft = hunk.Groups[4].Success ? int.Parse(hunk.Groups[4].Value) : 1;
                current.Lines.Add(new DiffLine(nextLine++, DiffLineKind.HunkHeader, null, null, line));
            }
        }

        // A stray "--- " line outside a git diff starts a builder that never gets a hunk; drop those.
        var files = builders
            .Where(b => b.FromGit || b.Lines.Count > 0)
            .Select((b, index) => new DiffFile(
                index,
                b.OldPath != "" ? b.OldPath : b.NewPath,
                b.NewPath != "" ? b.NewPath : b.OldPath,
                b.Kind,
                b.Lines.ToArray()))
            .ToList();

        if (files.Count == 0)
            throw new ReaderException("That text is not a unified diff.");
        return new DiffDocument(files);
    }

    /// <summary>The path on a "---" or "+++" line, or null for /dev/null.</summary>
    private static string? HeaderPath(string value)
    {
        var tab = value.IndexOf('\t');
        if (tab >= 0) value = value[..tab];
        value = Unquote(value.Trim());
        if (value == "/dev/null") return null;
        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value[2..]
            : value;
    }

    /// <summary>Decodes a git C-style quoted path; any other value is returned unchanged.</summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;

        var bytes = new List<byte>();
        var inner = value[1..^1];
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c != '\\' || i == inner.Length - 1)
            {
                // Keep a surrogate pair together so the code point survives encoding.
                var length = char.IsHighSurrogate(c) && i + 1 < inner.Length ? 2 : 1;
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(inner.Substring(i, length)));
                i += length - 1;
                continue;
            }

            c = inner[++i];
            switch (c)
            {
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'n': bytes.Add(10); break;
                case 'r': bytes.Add(13); break;
                case 't': bytes.Add(9); break;
                case 'v': bytes.Add(11); break;
                case >= '0' and <= '7':
                    var octal = c - '0';
                    for (var digits = 1; digits < 3 && i + 1 < inner.Length && inner[i + 1] is >= '0' and <= '7'; digits++)
                        octal = octal * 8 + (inner[++i] - '0');
                    bytes.Add((byte)octal);
                    break;
                default: // \\ and \" (and anything unknown) stand for themselves
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                    break;
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Splits the two path tokens of a "diff --git" line, each quoted or a bare run.</summary>
    private static (string, string)? SplitQuotedPair(string rest)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < rest.Length && tokens.Count < 2)
        {
            if (rest[i] == ' ') { i++; continue; }
            var start = i;
            if (rest[i] == '"')
            {
                i++;
                while (i < rest.Length && rest[i] != '"') i += rest[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, rest.Length);
            }
            else if (tokens.Count == 0)
            {
                // A bare first token ends at the space before the second token ("b/" or a quote).
                var next = rest.IndexOf(" b/", i, StringComparison.Ordinal);
                var quote = rest.IndexOf(" \"", i, StringComparison.Ordinal);
                i = next >= 0 && (quote < 0 || next < quote) ? next : quote >= 0 ? quote : rest.Length;
            }
            else
            {
                i = rest.Length;
            }
            tokens.Add(rest[start..i]);
        }
        return tokens.Count == 2 ? (tokens[0], tokens[1]) : null;
    }
}
