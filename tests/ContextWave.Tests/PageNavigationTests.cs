using ContextWave.Core;

namespace ContextWave.Tests;

public class PageNavigationTests
{
    [Theory]
    [InlineData("http://example.com/a", "http://example.com/a")]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    public void Web_links_open_in_the_browser(string uri, string expected) =>
        Assert.Equal(new PageNavigation.OpenExternal(expected), PageNavigation.Classify(uri));

    [Theory]
    [InlineData("mailto:a@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a")]
    [InlineData("data:text/html,hi")]
    [InlineData("ms-settings:privacy")]
    [InlineData("not a uri")]
    [InlineData("")]
    public void Only_http_and_https_links_open_in_the_browser(string uri) =>
        Assert.IsType<PageNavigation.Ignore>(PageNavigation.Classify(uri));

    [Theory]
    [InlineData("file:///C:/docs/a.md", @"C:\docs\a.md")]
    [InlineData("file:///C:/my%20docs/a.md", @"C:\my docs\a.md")]
    // A fragment is not part of the path.
    [InlineData("file:///C:/docs/a.md#intro", @"C:\docs\a.md")]
    // A dropped network file: opening it is the user's own act, the same as dropping it on the window.
    [InlineData("file://host/share/a.md", @"\\host\share\a.md")]
    [InlineData("file:////host/share/a.md", @"\\host\share\a.md")]
    [InlineData(@"file:\\host\share\a.md", @"\\host\share\a.md")]
    [InlineData("file://localhost/share/a.md", @"\\localhost\share\a.md")]
    public void File_links_on_a_drive_or_share_open_in_the_app(string uri, string expected) =>
        Assert.Equal(new PageNavigation.OpenFile(expected), PageNavigation.Classify(uri));

    [Theory]
    [InlineData("file:///%5C%5Chost%5Cshare%5Ca.md")]
    [InlineData(@"file://?/UNC/host/share/a.md")]
    [InlineData("file://./C:/a.md")]
    [InlineData("file:////?/C:/a.md")]
    [InlineData("file:////./C:/a.md")]
    [InlineData("file:///")]
    public void File_links_to_a_device_or_odd_path_are_ignored(string uri) =>
        Assert.IsType<PageNavigation.Ignore>(PageNavigation.Classify(uri));

    [Theory]
    [InlineData(@"C:\docs\a.md", true)]
    [InlineData("C:/docs/a.md", true)]
    [InlineData(@"\\host\share\a.md", true)]
    [InlineData(@"\\host\share", true)]
    [InlineData(@"\\?\C:\a.md", false)]
    [InlineData(@"\\?\UNC\host\share\a.md", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"\\host", false)]
    [InlineData(@"\\host\", false)]
    [InlineData(@"\\\share\a.md", false)]
    [InlineData(@"\docs\a.md", false)]
    [InlineData(@"docs\a.md", false)]
    [InlineData("C:", false)]
    [InlineData("", false)]
    public void Only_a_drive_or_share_path_is_openable(string path, bool expected) =>
        Assert.Equal(expected, PageNavigation.IsOpenablePath(path));
}