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
