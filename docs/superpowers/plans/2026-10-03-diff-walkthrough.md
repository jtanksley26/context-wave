# Diff Walkthrough Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Claude show a unified diff in MD Reader beside the explanation it reads aloud, with the diff scrolling to and highlighting the lines each sentence is about.

**Architecture:** A new UI-free `DiffDocument` in `MdReader.Core` parses a unified diff, renders it to HTML and resolves a `focus` string (`path`, `path:line`, `path:start-end`) to a range of rendered lines. `ReaderSession` gains a `show_diff` operation and an optional `focus` on `speak`, and remembers which sentence ids point at which range. The WebView page becomes a two-pane layout when a diff is loaded; `MainWindow` moves the diff focus when the spoken sentence's range changes. The Bridge exposes `show_diff` and the new `focus` parameter.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF, WebView2, Markdig, `ModelContextProtocol` C# SDK, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-diff-walkthrough-design.md`

## Notes for the implementer

- **Locked output folder.** `MdReader.App` and `MdReader.Bridge` build into `out/`. Any Claude session with the `md-reader` MCP server connected keeps `out\MdReader.Bridge.exe` and `out\MdReader.Core.dll` open, so a normal build fails with "file is being used by another process". Task 1 adds an `MdReaderOut` build property. Every command in this plan passes `-p:MdReaderOut=../../out-dev/` so builds go to `out-dev/` and never touch `out/`.
- Run commands from the repository root (`D:\Projects\md5reader`). They are written for Git Bash; they work unchanged in PowerShell.
- The test project references `MdReader.Core` and `MdReader.Bridge` only. `MdReader.App` has no automated tests; Tasks 9 and 10 are verified by building and by the manual checks in Task 11.
- `ReaderSession.Handle` runs on the UI thread (see `MainWindow`'s `PipeServer` handler). Nothing added here needs locking.
- `ImplicitUsings` does not include `System.Net`; files that call `WebUtility.HtmlEncode` need `using System.Net;`.
- Diff test data is built with `string.Join("\n", ...)` rather than raw string literals so that leading spaces on context lines survive editors that trim whitespace.
- Deviations from the spec, all recorded in the spec's "Amendments" section:
  - `DiffDocument.Resolve` has an `out string? problem` parameter so the session can report why a focus did not resolve.
  - A resolved range also takes in removed lines that sit directly before its first line, so a changed line shows its old and new text together.
  - Diff clicks are reported as `(fileIndex, lineIndex?)`: `DocumentView.DiffClicked` and `ReaderSession.SentenceForDiff`. This lets a header click work on a file with no lines (binary, pure rename).
  - The diff pane is refocused only when the spoken sentence's anchor differs from the previous one, so it does not fight manual scrolling.
  - `ReaderSession.DiffTitle` exposes the title for the window caption.

## File map

```
.gitignore                              add out-dev/
README.md                               tools list, dev build note
src/MdReader.Core/
  DiffDocument.cs        (new)          DiffFile, DiffLine, DiffAnchor; parse, render, resolve focus
  Protocol.cs                           PipeRequest.Diff/Title/Focus, PipeResult.DiffFiles
  ReaderSession.cs                      show_diff, focus on speak, anchors, click lookup
src/MdReader.App/
  MdReader.App.csproj                   MdReaderOut property
  DocumentView.cs                       two-pane page, SetDiff/FocusDiff/ClearDiffFocus, DiffClicked
  MainWindow.xaml.cs                    wiring, title, widen for diff
src/MdReader.Bridge/
  MdReader.Bridge.csproj                MdReaderOut property
  ReaderTools.cs                        show_diff tool, focus parameter, status line
tests/MdReader.Tests/
  SampleDiff.cs          (new)          shared diff text for tests
  DiffDocumentTests.cs   (new)
  ReaderToolsTests.cs    (new)
  ReaderSessionTests.cs                 new tests
  PipeTests.cs                          one new test
```

---

### Task 1: Development output folder

**Files:**
- Modify: `src/MdReader.App/MdReader.App.csproj`
- Modify: `src/MdReader.Bridge/MdReader.Bridge.csproj`
- Modify: `.gitignore`

- [ ] **Step 1: Make the App output folder overridable**

In `src/MdReader.App/MdReader.App.csproj` replace

```xml
    <OutputPath>..\..\out\</OutputPath>
```

with

```xml
    <MdReaderOut Condition="'$(MdReaderOut)' == ''">..\..\out\</MdReaderOut>
    <OutputPath>$(MdReaderOut)</OutputPath>
```

- [ ] **Step 2: Make the Bridge output folder overridable**

In `src/MdReader.Bridge/MdReader.Bridge.csproj` make the same replacement:

```xml
    <MdReaderOut Condition="'$(MdReaderOut)' == ''">..\..\out\</MdReaderOut>
    <OutputPath>$(MdReaderOut)</OutputPath>
```

- [ ] **Step 3: Ignore the folder**

In `.gitignore` add a line after `out/`:

```
out-dev/
```

- [ ] **Step 4: Verify the existing tests pass through the new folder**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: build succeeds, all tests pass, and `out-dev/MdReader.Bridge.exe` exists. `git status --short` shows only the three modified files.

- [ ] **Step 5: Commit**

```bash
git add .gitignore src/MdReader.App/MdReader.App.csproj src/MdReader.Bridge/MdReader.Bridge.csproj
git commit -m "build: allow the output folder to be overridden with MdReaderOut"
```

---

### Task 2: DiffDocument parsing

**Files:**
- Create: `src/MdReader.Core/DiffDocument.cs`
- Create: `tests/MdReader.Tests/SampleDiff.cs`
- Create: `tests/MdReader.Tests/DiffDocumentTests.cs`

- [ ] **Step 1: Write the shared test data**

Create `tests/MdReader.Tests/SampleDiff.cs`:

```csharp
namespace MdReader.Tests;

internal static class SampleDiff
{
    /// <summary>
    /// One modified file. Line indexes: 0 hunk, 1 context (1/1), 2 removed (old 2), 3 added (new 2),
    /// 4 added (new 3), 5 context (3/4), 6 context (4/5), 7 hunk, 8 context (10/11), 9 removed (old 11),
    /// 10 context (12/12).
    /// </summary>
    public static readonly string Foo = string.Join("\n",
        "diff --git a/src/Foo.cs b/src/Foo.cs",
        "index 1111111..2222222 100644",
        "--- a/src/Foo.cs",
        "+++ b/src/Foo.cs",
        "@@ -1,4 +1,5 @@",
        " using System;",
        "-var a = 1;",
        "+var a = 2;",
        "+var b = 3;",
        " ",
        " Console.WriteLine(a);",
        "@@ -10,3 +11,2 @@ void Tail()",
        " one",
        "-two",
        " three",
        "");

    /// <summary>
    /// Files: 0 src/Foo.cs (modified, lines 0-10), 1 tests/Foo.cs (added, lines 11-13),
    /// 2 docs/gone.md (deleted, lines 14-15), 3 img/logo.png (binary), 4 lib/Old.cs -> lib/New.cs (renamed).
    /// </summary>
    public static readonly string Many = Foo + string.Join("\n",
        "diff --git a/tests/Foo.cs b/tests/Foo.cs",
        "new file mode 100644",
        "index 0000000..3333333",
        "--- /dev/null",
        "+++ b/tests/Foo.cs",
        "@@ -0,0 +1,2 @@",
        "+first",
        "+second",
        "diff --git a/docs/gone.md b/docs/gone.md",
        "deleted file mode 100644",
        "index 4444444..0000000",
        "--- a/docs/gone.md",
        "+++ /dev/null",
        "@@ -1 +0,0 @@",
        "-bye",
        "diff --git a/img/logo.png b/img/logo.png",
        "index 5555555..6666666 100644",
        "Binary files a/img/logo.png and b/img/logo.png differ",
        "diff --git a/lib/Old.cs b/lib/New.cs",
        "similarity index 100%",
        "rename from lib/Old.cs",
        "rename to lib/New.cs",
        "");
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/MdReader.Tests/DiffDocumentTests.cs`:

```csharp
using MdReader.Core;

namespace MdReader.Tests;

public class DiffDocumentTests
{
    [Fact]
    public void Parses_a_modified_file_with_old_and_new_line_numbers()
    {
        var file = Assert.Single(DiffDocument.Parse(SampleDiff.Foo).Files);

        Assert.Equal((0, "src/Foo.cs", "src/Foo.cs", DiffFileKind.Modified),
            (file.Index, file.OldPath, file.NewPath, file.Kind));
        Assert.Equal(Enumerable.Range(0, 11), file.Lines.Select(l => l.Index));
        Assert.Equal(new DiffLine(0, DiffLineKind.HunkHeader, null, null, "@@ -1,4 +1,5 @@"), file.Lines[0]);
        Assert.Equal(new DiffLine(1, DiffLineKind.Context, 1, 1, "using System;"), file.Lines[1]);
        Assert.Equal(new DiffLine(2, DiffLineKind.Removed, 2, null, "var a = 1;"), file.Lines[2]);
        Assert.Equal(new DiffLine(3, DiffLineKind.Added, null, 2, "var a = 2;"), file.Lines[3]);
        Assert.Equal(new DiffLine(4, DiffLineKind.Added, null, 3, "var b = 3;"), file.Lines[4]);
        Assert.Equal(new DiffLine(5, DiffLineKind.Context, 3, 4, ""), file.Lines[5]);
        Assert.Equal(new DiffLine(7, DiffLineKind.HunkHeader, null, null, "@@ -10,3 +11,2 @@ void Tail()"),
            file.Lines[7]);
        Assert.Equal(new DiffLine(8, DiffLineKind.Context, 10, 11, "one"), file.Lines[8]);
        Assert.Equal(new DiffLine(9, DiffLineKind.Removed, 11, null, "two"), file.Lines[9]);
        Assert.Equal(new DiffLine(10, DiffLineKind.Context, 12, 12, "three"), file.Lines[10]);
    }

    [Fact]
    public void Parses_added_deleted_binary_and_renamed_files()
    {
        var files = DiffDocument.Parse(SampleDiff.Many).Files;

        Assert.Equal(
            new[]
            {
                DiffFileKind.Modified, DiffFileKind.Added, DiffFileKind.Deleted, DiffFileKind.Binary,
                DiffFileKind.Renamed,
            },
            files.Select(f => f.Kind));
        Assert.Equal(
            new[] { "src/Foo.cs", "tests/Foo.cs", "docs/gone.md", "img/logo.png", "lib/New.cs" },
            files.Select(f => f.DisplayPath));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, files.Select(f => f.Index));
        Assert.Equal(new[] { 11, 12, 13 }, files[1].Lines.Select(l => l.Index));
        Assert.Equal(new int?[] { null, 1, 2 }, files[1].Lines.Select(l => l.NewNumber));
        Assert.Equal(new DiffLine(15, DiffLineKind.Removed, 1, null, "bye"), files[2].Lines[1]);
        Assert.Empty(files[3].Lines);
        Assert.Equal(("lib/Old.cs", "lib/New.cs"), (files[4].OldPath, files[4].NewPath));
    }

    [Fact]
    public void Crlf_input_parses_the_same_as_lf()
    {
        var lf = DiffDocument.Parse(SampleDiff.Foo).Files[0].Lines;
        var crlf = DiffDocument.Parse(SampleDiff.Foo.Replace("\n", "\r\n")).Files[0].Lines;
        Assert.Equal(lf, crlf);
    }

    [Fact]
    public void No_newline_marker_is_not_a_line()
    {
        var diff = string.Join("\n",
            "--- a/x.txt",
            "+++ b/x.txt",
            "@@ -1 +1 @@",
            "-old",
            "\\ No newline at end of file",
            "+new",
            "\\ No newline at end of file");

        var lines = Assert.Single(DiffDocument.Parse(diff).Files).Lines;
        Assert.Equal(new[] { "@@ -1 +1 @@", "old", "new" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void Plain_headers_without_a_git_line_start_files_and_drop_timestamps()
    {
        var diff = string.Join("\n",
            "--- a/one.txt\t2026-01-01 00:00:00",
            "+++ b/one.txt\t2026-01-02 00:00:00",
            "@@ -1 +1 @@",
            "-a",
            "+b",
            "--- a/two.txt",
            "+++ b/two.txt",
            "@@ -1 +1 @@",
            "-c",
            "+d");

        Assert.Equal(new[] { "one.txt", "two.txt" }, DiffDocument.Parse(diff).Files.Select(f => f.DisplayPath));
    }

    [Fact]
    public void A_removed_line_that_looks_like_a_header_stays_in_its_hunk()
    {
        var diff = string.Join("\n",
            "--- a/x.sql",
            "+++ b/x.sql",
            "@@ -1,2 +1,2 @@",
            "--- old comment",
            "+-- new comment",
            " select 1;");

        var lines = Assert.Single(DiffDocument.Parse(diff).Files).Lines;
        Assert.Equal(
            new[] { DiffLineKind.HunkHeader, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context },
            lines.Select(l => l.Kind));
        Assert.Equal("-- old comment", lines[1].Text);
    }

    [Fact]
    public void A_blank_context_line_with_its_space_stripped_is_still_context()
    {
        var diff = string.Join("\n",
            "--- a/x.txt",
            "+++ b/x.txt",
            "@@ -1,3 +1,3 @@",
            " a",
            "",
            "-b",
            "+c");

        var lines = Assert.Single(DiffDocument.Parse(diff).Files).Lines;
        Assert.Equal(new DiffLine(2, DiffLineKind.Context, 2, 2, ""), lines[2]);
    }

    [Theory]
    [InlineData("just some text\nwith lines")]
    [InlineData("--- not a diff\nstill not")]
    [InlineData("diff --git a/x b/x\nindex 1..2")]
    public void Rejects_text_that_is_not_a_diff(string text)
    {
        var ex = Assert.Throws<ReaderException>(() => DiffDocument.Parse(text));
        Assert.Contains("not a unified diff", ex.Message);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~DiffDocumentTests"`
Expected: build fails with `error CS0103: The name 'DiffDocument' does not exist in the current context` (and similar for `DiffFileKind`, `DiffLine`).

- [ ] **Step 4: Write the parser**

Create `src/MdReader.Core/DiffDocument.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~DiffDocumentTests"`
Expected: PASS, 10 tests (7 facts and 3 theory cases).

- [ ] **Step 6: Commit**

```bash
git add src/MdReader.Core/DiffDocument.cs tests/MdReader.Tests/SampleDiff.cs tests/MdReader.Tests/DiffDocumentTests.cs
git commit -m "feat(core): parse unified diffs into files and numbered lines"
```

---

### Task 3: DiffDocument HTML

**Files:**
- Modify: `src/MdReader.Core/DiffDocument.cs`
- Modify: `tests/MdReader.Tests/DiffDocumentTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these tests to `DiffDocumentTests` (before the closing brace of the class):

```csharp
    [Fact]
    public void Html_has_a_section_per_file_and_a_row_per_line()
    {
        var html = DiffDocument.Parse(SampleDiff.Foo).Html;

        Assert.StartsWith(
            "<section class=\"df\" data-file=\"0\"><div class=\"dfh\"><span class=\"dfk\">modified</span>src/Foo.cs</div>",
            html);
        Assert.EndsWith("</section>", html);
        Assert.Contains(
            "<div class=\"dl ctx\" data-line=\"1\"><span class=\"dn\">1</span><span class=\"dn\">1</span>" +
            "<span class=\"dt\"> using System;</span></div>",
            html);
        Assert.Contains(
            "<div class=\"dl del\" data-line=\"2\"><span class=\"dn\">2</span><span class=\"dn\"></span>" +
            "<span class=\"dt\">-var a = 1;</span></div>",
            html);
        Assert.Contains(
            "<div class=\"dl add\" data-line=\"3\"><span class=\"dn\"></span><span class=\"dn\">2</span>" +
            "<span class=\"dt\">+var a = 2;</span></div>",
            html);
        Assert.Contains(
            "<div class=\"dl hunk\" data-line=\"7\"><span class=\"dn\"></span><span class=\"dn\"></span>" +
            "<span class=\"dt\">@@ -10,3 +11,2 @@ void Tail()</span></div>",
            html);
    }

    [Fact]
    public void Html_escapes_code_and_paths()
    {
        var diff = string.Join("\n",
            "--- a/<b>.cs",
            "+++ b/<b>.cs",
            "@@ -0,0 +1 @@",
            "+if (a < b && c == \"x\") {}");

        var html = DiffDocument.Parse(diff).Html;

        Assert.Contains("+if (a &lt; b &amp;&amp; c == &quot;x&quot;) {}", html);
        Assert.Contains("&lt;b&gt;.cs", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void Html_marks_binary_files_and_shows_both_names_of_a_rename()
    {
        var html = DiffDocument.Parse(SampleDiff.Many).Html;

        Assert.Contains(
            "<span class=\"dfk\">binary</span>img/logo.png</div><div class=\"dbin\">Binary file</div>", html);
        Assert.Contains("<span class=\"dfk\">renamed</span>lib/Old.cs → lib/New.cs</div>", html);
        Assert.Contains("<section class=\"df\" data-file=\"4\">", html);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~DiffDocumentTests"`
Expected: build fails with `error CS1061: 'DiffDocument' does not contain a definition for 'Html'`.

- [ ] **Step 3: Add the renderer**

In `src/MdReader.Core/DiffDocument.cs` add two usings at the top, above `using System.Text.RegularExpressions;`:

```csharp
using System.Net;
using System.Text;
```

Add a field and property directly below `public IReadOnlyList<DiffFile> Files { get; }`:

```csharp
    private string? _html;

    /// <summary>One section per file; each line carries its <see cref="DiffLine.Index"/> as data-line.</summary>
    public string Html => _html ??= Render();
```

Add this method at the end of the class, after `HeaderPath`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~DiffDocumentTests"`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/DiffDocument.cs tests/MdReader.Tests/DiffDocumentTests.cs
git commit -m "feat(core): render a parsed diff as HTML"
```

---

### Task 4: Focus resolution

**Files:**
- Modify: `src/MdReader.Core/DiffDocument.cs`
- Modify: `tests/MdReader.Tests/DiffDocumentTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `DiffDocumentTests`:

```csharp
    private static DiffAnchor? Resolve(string focus, out string? problem) =>
        DiffDocument.Parse(SampleDiff.Many).Resolve(focus, out problem);

    [Fact]
    public void Resolve_path_only_links_to_the_whole_file()
    {
        var anchor = Resolve("src/Foo.cs", out var problem);
        Assert.Equal(new DiffAnchor(0, null, null, "Foo.cs"), anchor);
        Assert.Null(problem);
    }

    [Fact]
    public void Resolve_single_line_uses_the_new_file_number()
    {
        Assert.Equal(new DiffAnchor(0, 4, 4, "Foo.cs:3"), Resolve("src/Foo.cs:3", out _));
    }

    [Fact]
    public void Resolve_range_takes_in_the_removed_lines_just_before_it()
    {
        // New lines 2-4 are rows 3-5; row 2 is the removed line they replace.
        Assert.Equal(new DiffAnchor(0, 2, 5, "Foo.cs:2-4"), Resolve("src/Foo.cs:2-4", out _));
    }

    [Fact]
    public void Resolve_range_includes_removed_lines_between_its_ends()
    {
        // New lines 11 and 12 are rows 8 and 10; row 9 is a removed line between them.
        Assert.Equal(new DiffAnchor(0, 8, 10, "Foo.cs:11-12"), Resolve("src/Foo.cs:11-12", out _));
    }

    [Fact]
    public void Resolve_accepts_a_reversed_range()
    {
        Assert.Equal(new DiffAnchor(0, 2, 5, "Foo.cs:2-4"), Resolve("src/Foo.cs:4-2", out _));
    }

    [Theory]
    [InlineData("b/src/Foo.cs")]
    [InlineData("a/src/Foo.cs")]
    [InlineData(@"src\Foo.cs")]
    [InlineData("./src/Foo.cs")]
    [InlineData("SRC/foo.CS")]
    [InlineData("  src/Foo.cs  ")]
    public void Resolve_normalises_the_path(string focus)
    {
        Assert.Equal(0, Resolve(focus, out _)!.FileIndex);
    }

    [Theory]
    [InlineData("New.cs", 4)]
    [InlineData("lib/Old.cs", 4)]
    [InlineData("gone.md", 2)]
    [InlineData("img/logo.png", 3)]
    public void Resolve_matches_the_end_of_a_path_and_old_names(string focus, int expectedFile)
    {
        Assert.Equal(expectedFile, Resolve(focus, out _)!.FileIndex);
    }

    [Fact]
    public void Resolve_reports_an_ambiguous_name_with_its_candidates()
    {
        Assert.Null(Resolve("Foo.cs:2", out var problem));
        Assert.Equal("Focus 'Foo.cs:2' matches more than one file: src/Foo.cs, tests/Foo.cs.", problem);
    }

    [Theory]
    [InlineData("nope.cs")]
    [InlineData("oo.cs")]
    [InlineData("")]
    [InlineData(":12")]
    public void Resolve_reports_an_unknown_path(string focus)
    {
        Assert.Null(Resolve(focus, out var problem));
        Assert.Equal($"Focus '{focus}' was not found in the diff.", problem);
    }

    [Theory]
    [InlineData("src/Foo.cs:900-910")]
    [InlineData("docs/gone.md:1")]
    public void Resolve_range_outside_the_diff_falls_back_to_the_file(string focus)
    {
        var anchor = Resolve(focus, out var problem);
        Assert.Null(anchor!.FirstLine);
        Assert.Null(anchor.LastLine);
        Assert.DoesNotContain(":", anchor.Label);
        Assert.Null(problem);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~DiffDocumentTests"`
Expected: build fails with `error CS0246: The type or namespace name 'DiffAnchor' could not be found`.

- [ ] **Step 3: Add the anchor type and resolver**

In `src/MdReader.Core/DiffDocument.cs` add this record after the `DiffFile` record:

```csharp
/// <summary>
/// A place in the diff. <see cref="FirstLine"/> and <see cref="LastLine"/> are <see cref="DiffLine.Index"/>
/// values; both are null when the anchor is a whole file.
/// </summary>
public sealed record DiffAnchor(int FileIndex, int? FirstLine, int? LastLine, string Label);
```

Add a third generated regex below `HunkHeader()`:

```csharp
    [GeneratedRegex(@"^(.*):(\d{1,9})(?:-(\d{1,9}))?$")]
    private static partial Regex FocusRange();
```

Add these methods after the `Html` property:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~DiffDocumentTests"`
Expected: PASS, 35 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/DiffDocument.cs tests/MdReader.Tests/DiffDocumentTests.cs
git commit -m "feat(core): resolve a focus string to a range of diff lines"
```

---

### Task 5: Pipe protocol fields

**Files:**
- Modify: `src/MdReader.Core/Protocol.cs`
- Modify: `tests/MdReader.Tests/PipeTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `PipeTests`, after `Request_and_response_round_trip`:

```csharp
    [Fact]
    public async Task Diff_fields_round_trip()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success(new PipeResult { Message = "ok", DiffFiles = 2 }));
        });
        server.Start();

        var response = await PipeClient.SendAsync(
            name,
            new PipeRequest { Op = "show_diff", Diff = SampleDiff.Many, Title = "PR 12", Focus = "src/Foo.cs:2-4" },
            2000);

        Assert.Equal(2, response.Result!.DiffFiles);
        Assert.Equal((SampleDiff.Many, "PR 12", "src/Foo.cs:2-4"), (seen!.Diff, seen.Title, seen.Focus));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~PipeTests"`
Expected: build fails with `error CS0117: 'PipeRequest' does not contain a definition for 'Diff'`.

- [ ] **Step 3: Add the fields**

In `src/MdReader.Core/Protocol.cs`, add to `PipeRequest` after `Mode`:

```csharp
    public string? Diff { get; init; }
    public string? Title { get; init; }
    public string? Focus { get; init; }
```

and to `PipeResult` after `TotalSentences`:

```csharp
    public int? DiffFiles { get; init; }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~PipeTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/Protocol.cs tests/MdReader.Tests/PipeTests.cs
git commit -m "feat(core): carry diff, title and focus over the pipe"
```

---

### Task 6: ReaderSession — show_diff and unloading

**Files:**
- Modify: `src/MdReader.Core/ReaderSession.cs`
- Modify: `tests/MdReader.Tests/ReaderSessionTests.cs`

- [ ] **Step 1: Write the failing tests**

In `tests/MdReader.Tests/ReaderSessionTests.cs`:

Add a field after `_appended`:

```csharp
    private readonly List<(string Html, string? Title)> _diffs = [];
```

Add a line at the end of the constructor:

```csharp
        _session.DiffReplaced += (html, title) => _diffs.Add((html, title));
```

Add a helper after `Speak`:

```csharp
    private PipeResponse ShowDiff(string diff, string? title = null) =>
        _session.Handle(new PipeRequest { Op = "show_diff", Diff = diff, Title = title });

    private PipeResult Status() => _session.Handle(new PipeRequest { Op = "status" }).Result!;
```

Add these tests at the end of the class:

```csharp
    [Fact]
    public void ShowDiff_stops_reading_clears_the_document_and_shows_the_diff()
    {
        Speak("First.");
        var response = ShowDiff(SampleDiff.Many, " PR 12 ");

        Assert.True(response.Ok, response.Error);
        Assert.Equal("Showing diff: 5 files.", response.Result!.Message);
        Assert.Equal(0, _queue.Count);
        Assert.Equal(ReadingState.Idle, _queue.State);
        Assert.Equal("stream", _session.Source);
        Assert.Equal("", _replaced.Last());
        Assert.Equal("PR 12", _session.DiffTitle);
        var (html, title) = Assert.Single(_diffs);
        Assert.Contains("data-line=\"0\"", html);
        Assert.Equal("PR 12", title);
    }

    [Fact]
    public void ShowDiff_reports_a_single_file_in_the_singular()
    {
        Assert.Equal("Showing diff: 1 file.", ShowDiff(SampleDiff.Foo).Result!.Message);
        Assert.Null(_session.DiffTitle);
    }

    [Fact]
    public void ShowDiff_does_not_need_the_voice()
    {
        _voiceReady = false;
        Assert.True(ShowDiff(SampleDiff.Foo).Ok);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   \n", "empty")]
    [InlineData("hello there", "not a unified diff")]
    public void ShowDiff_rejects_bad_input_and_leaves_the_reading_alone(string diff, string expected)
    {
        Speak("First.");
        var response = ShowDiff(diff);

        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error);
        Assert.Equal(1, _queue.Count);
        Assert.Equal(ReadingState.Playing, _queue.State);
        Assert.Empty(_diffs);
        Assert.Single(_replaced);
    }

    [Fact]
    public void ShowDiff_rejects_a_diff_over_the_limit()
    {
        var response = ShowDiff(new string('x', ReaderSession.MaxDiffBytes + 1));
        Assert.False(response.Ok);
        Assert.Contains("too large", response.Error);
    }

    [Fact]
    public void Speak_after_ShowDiff_appends_and_starts_reading()
    {
        ShowDiff(SampleDiff.Foo);
        Assert.True(Speak("One.").Ok);

        Assert.Equal(1, _queue.Count);
        Assert.Equal(ReadingState.Playing, _queue.State);
        Assert.Contains("data-sid=\"0\"", Assert.Single(_appended));
    }

    [Fact]
    public void A_second_ShowDiff_replaces_the_first()
    {
        ShowDiff(SampleDiff.Many);
        ShowDiff(SampleDiff.Foo);

        Assert.Equal(2, _diffs.Count);
        Assert.Equal(1, Status().DiffFiles);
    }

    [Fact]
    public void Status_reports_the_number_of_files_in_the_diff()
    {
        Assert.Equal(0, Status().DiffFiles);
        ShowDiff(SampleDiff.Many);
        Assert.Equal(5, Status().DiffFiles);
    }

    [Fact]
    public void Stop_unloads_the_diff()
    {
        ShowDiff(SampleDiff.Foo, "PR 12");
        _session.Handle(new PipeRequest { Op = "stop" });

        Assert.Equal(("", (string?)null), _diffs.Last());
        Assert.Null(_session.DiffTitle);
        Assert.Equal(0, Status().DiffFiles);
    }

    [Fact]
    public void Stop_without_a_diff_does_not_raise_DiffReplaced()
    {
        Speak("First.");
        _session.Handle(new PipeRequest { Op = "stop" });
        Assert.Empty(_diffs);
    }

    [Fact]
    public void Opening_a_file_unloads_the_diff()
    {
        ShowDiff(SampleDiff.Foo);
        Assert.True(ReadFile(Write("a.md", "One.")).Ok);

        Assert.Equal(("", (string?)null), _diffs.Last());
        Assert.Equal(0, Status().DiffFiles);
    }

    [Fact]
    public void A_failed_ReadFile_keeps_the_diff()
    {
        ShowDiff(SampleDiff.Foo);
        ReadFile(Path.Combine(_dir, "missing.md"));

        Assert.Single(_diffs);
        Assert.Equal(1, Status().DiffFiles);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderSessionTests"`
Expected: build fails with `error CS1061: 'ReaderSession' does not contain a definition for 'DiffReplaced'`.

- [ ] **Step 3: Implement show_diff**

In `src/MdReader.Core/ReaderSession.cs`:

Add a constant below `MaxFileBytes`:

```csharp
    public const int MaxDiffBytes = 2 * 1024 * 1024;
```

Add fields below `private MarkdownDocument _document = new();`:

```csharp
    private DiffDocument? _diff;

    // Sentence id -> the place in the diff its chunk was spoken about.
    private readonly Dictionary<int, DiffAnchor> _anchors = [];
```

Add a property below `Source`:

```csharp
    /// <summary>The title given with the loaded diff, or null.</summary>
    public string? DiffTitle { get; private set; }
```

Add an event below `DocumentAppended`:

```csharp
    /// <summary>The diff HTML and title; "" and null when the diff is unloaded.</summary>
    public event Action<string, string?>? DiffReplaced;
```

Add a case to the `switch` in `Handle`, after the `speak` case:

```csharp
                case "show_diff":
                    var files = ShowDiff(request.Diff ?? "", request.Title);
                    return PipeResponse.Success($"Showing diff: {files} {(files == 1 ? "file" : "files")}.");
```

In `OpenFile`, replace the last line `Replace(text, path);` with:

```csharp
        ClearDiff();
        Replace(text, path);
```

Add this method after `Speak`:

```csharp
    /// <summary>Starts a walkthrough: stops reading, clears the document and shows the diff.</summary>
    /// <returns>The number of files in the diff.</returns>
    public int ShowDiff(string diff, string? title)
    {
        if (string.IsNullOrWhiteSpace(diff)) throw new ReaderException("The diff is empty.");
        if (PipeProtocol.Utf8.GetByteCount(diff) > MaxDiffBytes)
            throw new ReaderException("Diff is too large (limit 2 MB).");
        // Parse before touching anything so a bad diff leaves the current reading alone.
        var parsed = DiffDocument.Parse(diff);

        queue.Stop();
        _document = new MarkdownDocument(announceCodeBlocks());
        _anchors.Clear();
        _diff = parsed;
        DiffTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Source = "stream";
        DocumentReplaced?.Invoke("");
        DiffReplaced?.Invoke(parsed.Html, DiffTitle);
        return parsed.Files.Count;
    }
```

Replace `Stop` with:

```csharp
    public void Stop()
    {
        queue.Stop();
        _document = new MarkdownDocument(announceCodeBlocks());
        Source = "";
        ClearDiff();
        DocumentReplaced?.Invoke("");
    }
```

In `Status`, add `DiffFiles` to the returned `PipeResult`:

```csharp
        return new PipeResult
        {
            Message = $"{state}, sentence {current} of {total}",
            State = state,
            Source = Source,
            CurrentSentence = current,
            TotalSentences = total,
            DiffFiles = _diff?.Files.Count ?? 0,
        };
```

Add this method before `RequireVoice`:

```csharp
    private void ClearDiff()
    {
        _anchors.Clear();
        if (_diff is null) return;
        _diff = null;
        DiffTitle = null;
        DiffReplaced?.Invoke("", null);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderSessionTests"`
Expected: PASS, including every test that existed before this task.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/ReaderSession.cs tests/MdReader.Tests/ReaderSessionTests.cs
git commit -m "feat(core): add the show_diff operation to the reader session"
```

---

### Task 7: ReaderSession — focus on speak and click lookup

**Files:**
- Modify: `src/MdReader.Core/ReaderSession.cs`
- Modify: `tests/MdReader.Tests/ReaderSessionTests.cs`

- [ ] **Step 1: Write the failing tests**

In `tests/MdReader.Tests/ReaderSessionTests.cs` replace the `Speak` helper with one that takes a focus:

```csharp
    private PipeResponse Speak(string text, string? mode = null, string? focus = null) =>
        _session.Handle(new PipeRequest { Op = "speak", Text = text, Mode = mode, Focus = focus });
```

Add these tests at the end of the class:

```csharp
    [Fact]
    public void Speak_with_focus_links_every_sentence_in_the_chunk_and_labels_it()
    {
        ShowDiff(SampleDiff.Foo);
        var response = Speak("One. Two.", focus: "Foo.cs:2-4");
        Speak("Three.");

        Assert.Equal("Queued 2 sentences.", response.Result!.Message);
        var expected = new DiffAnchor(0, 2, 5, "Foo.cs:2-4");
        Assert.Equal(expected, _session.AnchorFor(0));
        Assert.Equal(expected, _session.AnchorFor(1));
        Assert.Null(_session.AnchorFor(2));
        Assert.StartsWith("<div class=\"focus-label\">Foo.cs:2-4</div>", _appended[0]);
        Assert.DoesNotContain("focus-label", _appended[1]);
    }

    [Fact]
    public void Speak_with_a_focus_that_is_not_in_the_diff_still_reads_and_says_so()
    {
        ShowDiff(SampleDiff.Foo);
        var response = Speak("One.", focus: "Bar.cs:1");

        Assert.True(response.Ok);
        Assert.Equal("Queued 1 sentences. Focus 'Bar.cs:1' was not found in the diff.", response.Result!.Message);
        Assert.Equal(1, _queue.Count);
        Assert.Null(_session.AnchorFor(0));
        Assert.DoesNotContain("focus-label", _appended[0]);
    }

    [Fact]
    public void Speak_with_focus_but_no_diff_still_reads_and_says_so()
    {
        var response = Speak("One.", focus: "Foo.cs:1");

        Assert.True(response.Ok);
        Assert.Contains("no diff is loaded", response.Result!.Message);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public void Speak_replace_keeps_the_diff_and_drops_the_links()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        Speak("Again.", "replace", "Foo.cs:3");

        Assert.Single(_diffs);
        Assert.Equal(1, Status().DiffFiles);
        Assert.Equal(1, _queue.Count);
        Assert.Equal("Foo.cs:3", _session.AnchorFor(0)!.Label);
        Assert.StartsWith("<div class=\"focus-label\">Foo.cs:3</div>", _replaced.Last());
    }

    [Fact]
    public void A_new_diff_drops_the_links()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("One.", focus: "Foo.cs:2");
        ShowDiff(SampleDiff.Foo);

        Assert.Null(_session.AnchorFor(0));
        Assert.Null(_session.SentenceForDiff(0, null));
    }

    [Fact]
    public void SentenceForDiff_prefers_a_line_range_over_a_whole_file_link()
    {
        ShowDiff(SampleDiff.Many);
        Speak("Whole file.", focus: "src/Foo.cs");      // sentence 0, whole file
        Speak("The change.", focus: "src/Foo.cs:2-4");  // sentence 1, rows 2-5
        Speak("Once more.", focus: "src/Foo.cs:2");     // sentence 2, rows 2-3

        Assert.Equal(1, _session.SentenceForDiff(0, 3));
        Assert.Equal(1, _session.SentenceForDiff(0, 5));
        Assert.Equal(0, _session.SentenceForDiff(0, 9));
        Assert.Equal(0, _session.SentenceForDiff(0, null));
        Assert.Null(_session.SentenceForDiff(1, 12));
        Assert.Null(_session.SentenceForDiff(1, null));
    }

    [Fact]
    public void SentenceForDiff_ignores_lines_nobody_talked_about()
    {
        ShowDiff(SampleDiff.Foo);
        Speak("Intro.");
        Speak("The change.", focus: "Foo.cs:2-4");      // sentence 1, rows 2-5

        Assert.Null(_session.SentenceForDiff(0, 9));
        Assert.Equal(1, _session.SentenceForDiff(0, 4));
        Assert.Equal(1, _session.SentenceForDiff(0, null));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderSessionTests"`
Expected: build fails with `error CS1061: 'ReaderSession' does not contain a definition for 'AnchorFor'`.

- [ ] **Step 3: Implement focus and the lookups**

In `src/MdReader.Core/ReaderSession.cs`:

Add at the very top of the file:

```csharp
using System.Net;

```

Add a record after `ReaderException`:

```csharp
/// <param name="FocusProblem">Why the focus could not be linked to the diff, or null.</param>
public sealed record SpeakResult(int Count, string? FocusProblem);
```

Replace the `speak` case in `Handle` with:

```csharp
                case "speak":
                    RequireVoice();
                    var spoken = Speak(request.Text ?? "", request.Mode ?? "append", request.Focus);
                    var queued = $"Queued {spoken.Count} sentences.";
                    return PipeResponse.Success(
                        spoken.FocusProblem is null ? queued : $"{queued} {spoken.FocusProblem}");
```

Replace the whole `Speak` method with:

```csharp
    public SpeakResult Speak(string text, string mode, string? focus = null)
    {
        if (mode is not ("append" or "replace"))
            throw new ReaderException($"Unknown mode '{mode}'. Use 'append' or 'replace'.");
        if (string.IsNullOrWhiteSpace(text)) throw new ReaderException("The text is empty.");

        DiffAnchor? anchor = null;
        string? problem = null;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            if (_diff is null) problem = $"Focus '{focus}' was ignored because no diff is loaded.";
            else anchor = _diff.Resolve(focus, out problem);
        }

        var replace = mode == "replace" || Source == "";
        if (replace)
        {
            _document = new MarkdownDocument(announceCodeBlocks());
            _anchors.Clear();
        }

        var result = _document.Append(text);
        var html = result.Html;
        if (anchor is not null)
        {
            foreach (var sentence in result.Sentences) _anchors[sentence.Id] = anchor;
            html = $"<div class=\"focus-label\">{WebUtility.HtmlEncode(anchor.Label)}</div>{html}";
        }

        if (replace)
        {
            Source = "stream";
            DocumentReplaced?.Invoke(html);
            queue.Load(result.Sentences, voiceReady());
        }
        else
        {
            DocumentAppended?.Invoke(html);
            queue.Append(result.Sentences, voiceReady());
        }
        return new SpeakResult(result.Sentences.Count, problem);
    }
```

Add these methods after `ShowDiff`:

```csharp
    /// <summary>The place in the diff that sentence was spoken about, or null.</summary>
    public DiffAnchor? AnchorFor(int sentenceId) => _anchors.GetValueOrDefault(sentenceId);

    /// <summary>
    /// The first sentence to jump to for a click in the diff. With a line: a sentence whose range covers
    /// it, otherwise one linked to the whole file. Without a line (the file header): any sentence linked
    /// to that file.
    /// </summary>
    public int? SentenceForDiff(int fileIndex, int? lineIndex)
    {
        int? inRange = null, wholeFile = null, any = null;
        foreach (var (id, anchor) in _anchors)
        {
            if (anchor.FileIndex != fileIndex) continue;
            any = Lower(any, id);
            if (anchor.FirstLine is null) wholeFile = Lower(wholeFile, id);
            else if (lineIndex >= anchor.FirstLine && lineIndex <= anchor.LastLine) inRange = Lower(inRange, id);
        }
        return lineIndex is null ? any : inRange ?? wholeFile;

        static int Lower(int? current, int id) => current is { } value && value < id ? value : id;
    }
```

The private `Replace(string markdown, string source)` method stays as it is; `OpenFile` still uses it.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderSessionTests"`
Expected: PASS, including `Speak_starts_a_stream_and_then_appends`, `Speak_replace_starts_over` and `Speak_rejects_bad_input` from before this task.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.Core/ReaderSession.cs tests/MdReader.Tests/ReaderSessionTests.cs
git commit -m "feat(core): link spoken sentences to diff lines with a focus"
```

---

### Task 8: Bridge tools

**Files:**
- Modify: `src/MdReader.Bridge/ReaderTools.cs`
- Create: `tests/MdReader.Tests/ReaderToolsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/ReaderToolsTests.cs`:

```csharp
using MdReader.Bridge;
using MdReader.Core;

namespace MdReader.Tests;

public class ReaderToolsTests
{
    private static string NewName() => $"MdReader.Test.{Guid.NewGuid():N}";

    private static ReaderTools Tools(string name) => new(new AppLink(name, () => false, TimeSpan.FromSeconds(2)));

    [Fact]
    public async Task ShowDiff_sends_the_diff_and_title()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Showing diff: 1 file."));
        });
        server.Start();

        var text = await Tools(name).ShowDiff(SampleDiff.Foo, "PR 12", CancellationToken.None);

        Assert.Equal("Showing diff: 1 file.", text);
        Assert.Equal(("show_diff", SampleDiff.Foo, "PR 12"), (seen!.Op, seen.Diff, seen.Title));
    }

    [Fact]
    public async Task Speak_sends_the_focus()
    {
        var name = NewName();
        PipeRequest? seen = null;
        using var server = new PipeServer(name, request =>
        {
            seen = request;
            return Task.FromResult(PipeResponse.Success("Queued 1 sentences."));
        });
        server.Start();

        await Tools(name).Speak("Hello.", "append", "src/Foo.cs:2-4", CancellationToken.None);

        Assert.Equal(("speak", "Hello.", "append", "src/Foo.cs:2-4"), (seen!.Op, seen.Text, seen.Mode, seen.Focus));
    }

    [Theory]
    [InlineData(0, "state: playing\nsource: stream\nsentence: 1 of 2")]
    [InlineData(1, "state: playing\nsource: stream\nsentence: 1 of 2\ndiff: 1 file")]
    [InlineData(3, "state: playing\nsource: stream\nsentence: 1 of 2\ndiff: 3 files")]
    public async Task Status_mentions_a_loaded_diff(int diffFiles, string expected)
    {
        var name = NewName();
        using var server = new PipeServer(name, _ => Task.FromResult(PipeResponse.Success(new PipeResult
        {
            Message = "playing, sentence 1 of 2",
            State = "playing",
            Source = "stream",
            CurrentSentence = 1,
            TotalSentences = 2,
            DiffFiles = diffFiles,
        })));
        server.Start();

        Assert.Equal(expected, await Tools(name).Status(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderToolsTests"`
Expected: build fails with `error CS1061: 'ReaderTools' does not contain a definition for 'ShowDiff'`.

- [ ] **Step 3: Add the tool and the parameter**

In `src/MdReader.Bridge/ReaderTools.cs`:

Replace the whole `Speak` method and its attributes with:

```csharp
    [McpServerTool(Name = "speak")]
    [Description(
        "Read markdown text aloud in the MD Reader window. Call it repeatedly to stream: with mode 'append' " +
        "each call adds to the end of the current document and reading continues without a gap. " +
        "Send whole paragraphs, not fragments of a sentence. " +
        "During a code walkthrough (after show_diff), pass focus so the diff follows what is being said. " +
        "Returns as soon as the text is queued; it does not wait for it to be spoken.")]
    public Task<string> Speak(
        [Description("Markdown text to read.")] string text,
        [Description(
            "'append' (default) adds to the current document; 'replace' interrupts the current reading " +
            "and starts a new document.")]
        string mode = "append",
        [Description(
            "Optional. The part of the shown diff this text is about: 'path', 'path:line' or " +
            "'path:start-end', using the path as it appears in the diff and line numbers from the new " +
            "version of the file. One call, one focus: start a new call when you move to another place.")]
        string? focus = null,
        CancellationToken ct = default) =>
        Call(new PipeRequest { Op = "speak", Text = text, Mode = mode, Focus = focus }, ct);

    [McpServerTool(Name = "show_diff")]
    [Description(
        "Show a unified diff in the MD Reader window beside the text being read. Use it when reviewing a " +
        "pull request or walking the user through code changes: call show_diff once with the whole diff " +
        "(for example the output of 'git diff' or 'gh pr diff'), then call speak once per point, each " +
        "with a focus naming the file and lines that point is about. Replaces the current document and " +
        "any earlier diff. Returns as soon as the diff is displayed.")]
    public Task<string> ShowDiff(
        [Description("Unified diff text, at most 2 MB.")] string diff,
        [Description("Optional short title for the window, such as the pull request name.")]
        string? title = null,
        CancellationToken ct = default) =>
        Call(new PipeRequest { Op = "show_diff", Diff = diff, Title = title }, ct);
```

Replace the `Status` description with:

```csharp
    [Description(
        "Report whether MD Reader is playing, paused or idle, what it is reading, its position, and " +
        "whether a diff is shown.")]
```

At the end of `Call`, replace

```csharp
        return result.State is null
            ? result.Message
            : $"state: {result.State}\nsource: {result.Source}\nsentence: {result.CurrentSentence} of {result.TotalSentences}";
```

with

```csharp
        if (result.State is null) return result.Message;
        var status =
            $"state: {result.State}\nsource: {result.Source}\nsentence: {result.CurrentSentence} of {result.TotalSentences}";
        return result.DiffFiles is int files and > 0
            ? $"{status}\ndiff: {files} {(files == 1 ? "file" : "files")}"
            : status;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~ReaderToolsTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 6: Commit**

```bash
git add src/MdReader.Bridge/ReaderTools.cs tests/MdReader.Tests/ReaderToolsTests.cs
git commit -m "feat(bridge): add show_diff and a focus parameter on speak"
```

---

### Task 9: Two-pane document view

**Files:**
- Modify: `src/MdReader.App/DocumentView.cs`

There are no automated tests for the App project. This task is verified by a build here and by the manual checks in Task 11.

- [ ] **Step 1: Replace the shell page**

In `src/MdReader.App/DocumentView.cs` replace the whole `ShellHtml` constant with:

```csharp
    private const string ShellHtml = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
        <style>
          :root { color-scheme: light dark; --bg:#ffffff; --fg:#1f2328; --hl:#fff3a3; --line:#d0d7de; --code:#f6f8fa;
                  --add:#e6ffec; --del:#ffebe9; --focus:#bf8700; --focusbg:rgba(255,212,0,.22); }
          @media (prefers-color-scheme: dark) {
            :root { --bg:#1e1e1e; --fg:#e6e6e6; --hl:#5c4b00; --line:#444444; --code:#2a2a2a;
                    --add:#12361f; --del:#4a1d1d; --focus:#e3b341; --focusbg:rgba(227,179,65,.2); }
          }
          html { height:100%; }
          body { background:var(--bg); color:var(--fg); font:17px/1.65 "Segoe UI",sans-serif;
                 max-width:760px; margin:0 auto; padding:24px 32px 40vh; }
          [data-sid] { cursor:pointer; border-radius:3px; }
          .speaking { background:var(--hl); }
          pre { background:var(--code); padding:12px; overflow:auto; border-radius:6px; }
          code { font-family:Consolas,monospace; font-size:.92em; }
          pre code[data-sid] { display:block; }
          table { border-collapse:collapse; }
          th, td { border:1px solid var(--line); padding:4px 10px; }
          blockquote { border-left:4px solid var(--line); margin-left:0; padding-left:16px; }
          a { color:inherit; }
          #empty { opacity:.6; margin-top:30vh; text-align:center; }

          /* Without a diff the page is the single centred column above. */
          #diff, #divider { display:none; }
          body.split { max-width:none; margin:0; padding:0; height:100%; display:flex; overflow:hidden; }
          body.split #diff { display:block; flex:0 0 var(--diffw, 55%); min-width:0; overflow:auto;
                             font:13px/1.5 Consolas,monospace; }
          body.split #divider { display:block; flex:0 0 6px; cursor:col-resize; background:var(--line); }
          body.split #text { flex:1 1 0; min-width:0; overflow:auto; padding:24px 32px 40vh; }
          body.split #empty { display:none; }

          #diffTitle { padding:8px 10px; font:600 14px "Segoe UI",sans-serif; }
          #diffTitle:empty { display:none; }
          .df { min-width:max-content; margin-bottom:18px; }
          .dfh { position:sticky; top:0; padding:6px 10px; font-weight:600; cursor:pointer;
                 background:var(--code); border-top:1px solid var(--line); border-bottom:1px solid var(--line); }
          .dfk { font-weight:400; opacity:.7; margin-right:8px; }
          .dbin { padding:6px 10px; opacity:.7; }
          .dl { display:flex; white-space:pre; cursor:pointer; }
          .dn { flex:0 0 5ch; text-align:right; padding-right:8px; opacity:.55; user-select:none; }
          .dt { flex:1 0 auto; padding:0 12px 0 4px; tab-size:4; }
          .dl.add { background-color:var(--add); }
          .dl.del { background-color:var(--del); }
          .dl.hunk { background-color:var(--code); opacity:.75; }
          .focused { box-shadow:inset 4px 0 0 var(--focus);
                     background-image:linear-gradient(var(--focusbg), var(--focusbg)); }
          .focus-label { font:12px Consolas,monospace; opacity:.7; margin:22px 0 -10px; }
        </style>
        </head>
        <body>
        <div id="diff"><div id="diffTitle"></div><div id="diffBody"></div></div>
        <div id="divider"></div>
        <div id="text">
          <div id="doc"></div>
          <div id="empty">Open a markdown file, drop one here, or ask Claude to read to you.</div>
        </div>
        <script>
          const doc = document.getElementById('doc');
          const empty = document.getElementById('empty');
          const textPane = document.getElementById('text');
          const diffPane = document.getElementById('diff');
          const diffTitle = document.getElementById('diffTitle');
          const diffBody = document.getElementById('diffBody');
          const divider = document.getElementById('divider');

          function setDoc(html) {
            doc.innerHTML = html;
            empty.style.display = html ? 'none' : '';
            window.scrollTo(0, 0);
            textPane.scrollTop = 0;
          }
          function appendDoc(html) {
            doc.insertAdjacentHTML('beforeend', html);
            empty.style.display = 'none';
          }
          function highlight(id) {
            document.querySelectorAll('.speaking').forEach(e => e.classList.remove('speaking'));
            const parts = document.querySelectorAll('[data-sid="' + id + '"]');
            parts.forEach(e => e.classList.add('speaking'));
            if (parts.length) parts[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
          }

          function setDiff(html, title) {
            diffBody.innerHTML = html;
            diffTitle.textContent = title;
            document.body.classList.toggle('split', html !== '');
            diffPane.scrollTop = 0;
            diffPane.scrollLeft = 0;
          }
          function clearDiffFocus() {
            diffBody.querySelectorAll('.focused').forEach(e => e.classList.remove('focused'));
          }
          // first and last are data-line values, or null to focus the file header.
          function focusDiff(file, first, last) {
            clearDiffFocus();
            let target = null;
            if (first === null) {
              target = diffBody.querySelector('[data-file="' + file + '"] .dfh');
              if (target) target.classList.add('focused');
            } else {
              for (let i = first; i <= last; i++) {
                const row = diffBody.querySelector('.dl[data-line="' + i + '"]');
                if (!row) continue;
                row.classList.add('focused');
                target = target || row;
              }
            }
            if (target) target.scrollIntoView({ behavior: 'smooth', block: first === null ? 'start' : 'center' });
          }

          document.addEventListener('click', e => {
            if (e.target.closest('a')) return;
            const row = e.target.closest('.dl');
            const head = e.target.closest('.dfh');
            if (row || head) {
              const section = (row || head).closest('[data-file]');
              window.chrome.webview.postMessage('diff:' + section.dataset.file + ':' + (row ? row.dataset.line : ''));
              return;
            }
            const el = e.target.closest('[data-sid]');
            if (el) window.chrome.webview.postMessage(el.dataset.sid);
          });

          divider.addEventListener('pointerdown', e => {
            divider.setPointerCapture(e.pointerId);
            e.preventDefault();
          });
          divider.addEventListener('pointermove', e => {
            if (!divider.hasPointerCapture(e.pointerId)) return;
            const percent = Math.min(75, Math.max(25, e.clientX / window.innerWidth * 100));
            document.body.style.setProperty('--diffw', percent + '%');
          });
        </script>
        </body>
        </html>
        """;
```

- [ ] **Step 2: Add the event and the message handling**

Below `public event Action<string>? FileDropped;` add:

```csharp
    /// <summary>A click in the diff: the file index, and the line index or null for the file header.</summary>
    public event Action<int, int?>? DiffClicked;
```

Replace the `core.WebMessageReceived` handler in `InitializeAsync` with:

```csharp
        core.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
```

Add this method after `Run`:

```csharp
    /// <summary>The page posts a sentence id, or "diff:{file}:{line}" with the line empty for a header.</summary>
    private void OnMessage(string message)
    {
        if (int.TryParse(message, out var id))
        {
            SentenceClicked?.Invoke(id);
            return;
        }

        var parts = message.Split(':');
        if (parts is not ["diff", var file, var line] || !int.TryParse(file, out var fileIndex)) return;
        if (line == "") DiffClicked?.Invoke(fileIndex, null);
        else if (int.TryParse(line, out var lineIndex)) DiffClicked?.Invoke(fileIndex, lineIndex);
    }
```

- [ ] **Step 3: Add the diff methods**

Below `public void Highlight(int sentenceId) => Run($"highlight({sentenceId})");` add:

```csharp
    /// <summary>Shows the diff pane with this HTML, or hides it when the HTML is empty.</summary>
    public void SetDiff(string html, string? title) =>
        Run($"setDiff({JsonSerializer.Serialize(html)}, {JsonSerializer.Serialize(title ?? "")})");

    public void FocusDiff(DiffAnchor anchor) =>
        Run($"focusDiff({anchor.FileIndex}, {Js(anchor.FirstLine)}, {Js(anchor.LastLine)})");

    public void ClearDiffFocus() => Run("clearDiffFocus()");

    private static string Js(int? value) => value?.ToString() ?? "null";
```

- [ ] **Step 4: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.App/DocumentView.cs
git commit -m "feat(app): add a diff pane to the document view"
```

---

### Task 10: Window wiring

**Files:**
- Modify: `src/MdReader.App/MainWindow.xaml.cs`

- [ ] **Step 1: Add the fields**

Below `private bool _downloading;` add:

```csharp
    private const double DiffWindowWidth = 1300;

    // The place in the diff currently shown as focused; the pane is only moved when this changes,
    // so it does not scroll back on every sentence while the user looks around.
    private DiffAnchor? _focused;
```

- [ ] **Step 2: Wire the session and view events**

In the constructor, below the `_session.DocumentAppended += ...` block, add:

```csharp
        _session.DiffReplaced += (html, title) =>
        {
            _focused = null;
            _view.SetDiff(html, title);
            UpdateTitle();
            if (html != "") WidenForDiff();
        };
```

Replace the `_queue.SentenceStarted` handler with:

```csharp
        _queue.SentenceStarted += id => Dispatcher.InvokeAsync(() =>
        {
            _view.Highlight(id);
            FollowDiff(id);
            UpdatePosition();
        });
```

Below the `_view.SentenceClicked += ...` block add:

```csharp
        _view.DiffClicked += (file, line) =>
        {
            if (VoiceReady() && _session.SentenceForDiff(file, line) is { } id) _queue.JumpTo(id);
        };
```

- [ ] **Step 3: Add the helpers and update the title**

Replace `UpdateTitle` with:

```csharp
    private void UpdateTitle() => Title = _session.DiffTitle is { } title
        ? $"{title} - MD Reader"
        : _session.Source switch
        {
            "" => "MD Reader",
            "stream" => "MD Reader - from Claude",
            var path => $"{Path.GetFileName(path)} - MD Reader",
        };
```

Add these methods after `UpdatePosition`:

```csharp
    private void FollowDiff(int sentenceId)
    {
        var anchor = _session.AnchorFor(sentenceId);
        if (anchor == _focused) return;
        _focused = anchor;
        if (anchor is null) _view.ClearDiffFocus();
        else _view.FocusDiff(anchor);
    }

    /// <summary>Two panes need more room than one; widen a narrow window, staying on the screen.</summary>
    private void WidenForDiff()
    {
        if (WindowState != WindowState.Normal || Width >= DiffWindowWidth) return;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(DiffWindowWidth, area.Width);
        if (Left + Width > area.Right) Left = Math.Max(area.Left, area.Right - Width);
    }
```

- [ ] **Step 4: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.App/MainWindow.xaml.cs
git commit -m "feat(app): follow the spoken sentence in the diff pane"
```

---

### Task 11: Documentation and manual verification

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the README**

In `README.md` replace the line

```
Tools: `read_file(path)`, `speak(text, mode)`, `stop()`, `status()`.
```

with

```
Tools: `read_file(path)`, `speak(text, mode, focus)`, `show_diff(diff, title)`, `stop()`, `status()`.

For a code walkthrough, Claude calls `show_diff` with a unified diff and then `speak` with a
`focus` such as `src/Foo.cs:120-140`. The window shows the diff beside the explanation and
scrolls it to the lines being talked about. Clicking a diff line jumps to its explanation.
```

At the end of the "Build" section, after the sentence about closing sessions before rebuilding, add:

````markdown
To build and test while those are open, send the output somewhere else:

```bash
dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"
```
````

- [ ] **Step 2: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 3: Manual check with the development build**

Close the MD Reader window if it is open (only one instance can own the pipe), then start the development build:

```bash
./out-dev/MdReader.App.exe
```

Send it a diff and two chunks of explanation straight over the pipe, from PowerShell in the repository root:

```powershell
function Send-Reader($request) {
    $name = "MdReader.$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, 'InOut')
    $pipe.Connect(2000)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.WriteLine(($request | ConvertTo-Json -Compress))
    $writer.Flush()
    (New-Object System.IO.StreamReader($pipe)).ReadLine()
    $pipe.Dispose()
}
$diff = (git diff main...HEAD -- src/MdReader.Core/ReaderSession.cs src/MdReader.Bridge/ReaderTools.cs) -join "`n"
Send-Reader @{ op = 'show_diff'; diff = $diff; title = 'Diff walkthrough' }
Send-Reader @{ op = 'speak'; text = 'The session now accepts a diff. This is the new operation.'; focus = 'ReaderSession.cs:30-40' }   # use a line range the diff shows
Send-Reader @{ op = 'speak'; text = 'The bridge exposes it as a tool.'; focus = 'ReaderTools.cs' }
Send-Reader @{ op = 'speak'; text = 'That is the whole change.' }
Send-Reader @{ op = 'status' }
```

Check each of these:

- The window widens and splits: diff on the left with file headers, line numbers and green/red rows; text on the right. The title bar reads "Diff walkthrough - MD Reader".
- While the first chunk is read, rows in `ReaderSession.cs` are highlighted and scrolled into view, and the text shows the label for that range (for example `ReaderSession.cs:30-40`) above the chunk.
- On the second chunk the highlight moves to the `ReaderTools.cs` header. On the third it clears and the diff stays where it was.
- Scrolling the diff by hand during a chunk is not undone when the next sentence of the same chunk starts.
- Clicking a highlighted-range row in `ReaderSession.cs` jumps reading back to the first chunk. Clicking the `ReaderTools.cs` header jumps to the second. Clicking a row nobody talked about does nothing.
- Dragging the divider resizes the panes and stops at about a quarter and three quarters of the width.
- The `status` reply includes `"diffFiles":2`.
- Switching Windows between light and dark mode keeps the diff readable.
- `Send-Reader @{ op = 'stop' }` returns the window to the single centred column with the "Open a markdown file" message. Opening a `.md` file with File > Open while a diff is shown does the same and reads the file.
- `Send-Reader @{ op = 'show_diff'; diff = 'hello' }` returns `"ok":false` with "not a unified diff" and changes nothing on screen.

Fix anything that fails before continuing; commit fixes separately with a `fix(app):` message.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: describe show_diff and the development output folder"
```

- [ ] **Step 5: Hand over for the real build**

The registered MCP server still runs the old bridge from `out/`. Tell the user that to use the new tools they need to close MD Reader and every Claude session that has `md-reader` connected, run `dotnet build MdReader.sln`, and start Claude again. This step cannot be done from inside a session that is using the bridge.
