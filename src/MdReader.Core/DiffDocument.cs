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
                var git = GitHeader().Match(line);
                if (git.Success)
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
                current.OldPath = line["rename from ".Length..];
                current.Kind = DiffFileKind.Renamed;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                current.NewPath = line["rename to ".Length..];
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
                b.Lines))
            .ToList();

        if (!files.Any(f => f.Lines.Count > 0 || f.Kind == DiffFileKind.Binary))
            throw new ReaderException("That text is not a unified diff.");
        return new DiffDocument(files);
    }

    /// <summary>The path on a "---" or "+++" line, or null for /dev/null.</summary>
    private static string? HeaderPath(string value)
    {
        var tab = value.IndexOf('\t');
        if (tab >= 0) value = value[..tab];
        value = value.Trim();
        if (value == "/dev/null") return null;
        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value[2..]
            : value;
    }
}
