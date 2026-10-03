using System.Net;
using System.Text;
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

/// <summary>
/// A place in the diff. <see cref="FirstLine"/> and <see cref="LastLine"/> are <see cref="DiffLine.Index"/>
/// values; both are null when the anchor is a whole file.
/// </summary>
public sealed record DiffAnchor(int FileIndex, int? FirstLine, int? LastLine, string Label);

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

    [GeneratedRegex(@"^(.*):(\d{1,9})(?:-(\d{1,9}))?$")]
    private static partial Regex FocusRange();

    private DiffDocument(IReadOnlyList<DiffFile> files) => Files = files;

    public IReadOnlyList<DiffFile> Files { get; }

    private string? _html;

    /// <summary>One section per file; each line carries its <see cref="DiffLine.Index"/> as data-line.</summary>
    public string Html => _html ??= Render();

    /// <summary>
    /// Resolves "path", "path:line" or "path:start-end" (new-file line numbers). Returns null and sets
    /// <paramref name="problem"/> when the path matches no file or more than one.
    /// </summary>
    public DiffAnchor? Resolve(string focus, out string? problem)
    {
        problem = null;
        var path = focus.Trim();
        int? start = null, end = null;
        if (FocusRange().Match(path) is { Success: true } range)
        {
            path = range.Groups[1].Value;
            start = int.Parse(range.Groups[2].Value);
            end = range.Groups[3].Success ? int.Parse(range.Groups[3].Value) : start;
            if (end < start) (start, end) = (end, start);
        }

        var matches = FindFiles(path);
        if (matches.Count == 0)
        {
            problem = $"Focus '{focus}' was not found in the diff.";
            return null;
        }
        if (matches.Count > 1)
        {
            var names = string.Join(", ", matches.Select(f => f.DisplayPath));
            problem = $"Focus '{focus}' matches more than one file: {names}.";
            return null;
        }

        var file = matches[0];
        var name = file.DisplayPath[(file.DisplayPath.LastIndexOf('/') + 1)..];
        if (start is null) return new DiffAnchor(file.Index, null, null, name);

        var first = -1;
        var last = -1;
        for (var i = 0; i < file.Lines.Count; i++)
        {
            if (file.Lines[i].NewNumber is not { } number || number < start || number > end) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return new DiffAnchor(file.Index, null, null, name);

        // A changed line is shown as its removed text followed by its added text; keep them together.
        while (first > 0 && file.Lines[first - 1].Kind == DiffLineKind.Removed) first--;

        var label = start == end ? $"{name}:{start}" : $"{name}:{start}-{end}";
        return new DiffAnchor(file.Index, file.Lines[first].Index, file.Lines[last].Index, label);
    }

    private List<DiffFile> FindFiles(string path)
    {
        var query = path.Trim().Replace('\\', '/');
        if (query.StartsWith("./", StringComparison.Ordinal)) query = query[2..];
        var found = MatchFiles(query);
        if (found.Count == 0
            && (query.StartsWith("a/", StringComparison.Ordinal) || query.StartsWith("b/", StringComparison.Ordinal)))
            found = MatchFiles(query[2..]);
        return found;
    }

    private List<DiffFile> MatchFiles(string query)
    {
        if (query.Length == 0) return [];
        var exact = Files.Where(f => Named(f, p => p.Equals(query, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) return exact;
        var tail = "/" + query;
        return Files.Where(f => Named(f, p => p.EndsWith(tail, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static bool Named(DiffFile file, Func<string, bool> test) => test(file.NewPath) || test(file.OldPath);

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

    private string Render()
    {
        var html = new StringBuilder();
        foreach (var file in Files)
        {
            var name = file.Kind == DiffFileKind.Renamed && file.OldPath != file.NewPath
                ? $"{WebUtility.HtmlEncode(file.OldPath)} → {WebUtility.HtmlEncode(file.NewPath)}"
                : WebUtility.HtmlEncode(file.DisplayPath);
            html.Append($"<section class=\"df\" data-file=\"{file.Index}\">");
            html.Append(
                $"<div class=\"dfh\"><span class=\"dfk\">{file.Kind.ToString().ToLowerInvariant()}</span>{name}</div>");
            if (file.Kind == DiffFileKind.Binary) html.Append("<div class=\"dbin\">Binary file</div>");

            foreach (var line in file.Lines)
            {
                var (css, marker) = line.Kind switch
                {
                    DiffLineKind.Added => ("add", "+"),
                    DiffLineKind.Removed => ("del", "-"),
                    DiffLineKind.HunkHeader => ("hunk", ""),
                    _ => ("ctx", " "),
                };
                html.Append($"<div class=\"dl {css}\" data-line=\"{line.Index}\">");
                html.Append($"<span class=\"dn\">{line.OldNumber}</span><span class=\"dn\">{line.NewNumber}</span>");
                html.Append($"<span class=\"dt\">{marker}{WebUtility.HtmlEncode(line.Text)}</span></div>");
            }

            html.Append("</section>");
        }
        return html.ToString();
    }
}
