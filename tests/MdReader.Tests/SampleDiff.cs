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
