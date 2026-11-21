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

    [Fact]
    public void File_links_on_a_drive_open_in_the_app() =>
        Assert.Equal(new PageNavigation.OpenFile(@"C:\docs\a.md"), PageNavigation.Classify("file:///C:/docs/a.md"));

    [Theory]
    [InlineData("file://host/share/a.md")]
    [InlineData("file:////host/share/a.md")]
    [InlineData(@"file:\\host\share\a.md")]
    [InlineData("file://localhost/share/a.md")]
    [InlineData("file:///%5C%5Chost%5Cshare%5Ca.md")]
    [InlineData(@"file://?/UNC/host/share/a.md")]
    [InlineData("file:///")]
    public void File_links_to_a_network_path_are_ignored(string uri) =>
        Assert.IsType<PageNavigation.Ignore>(PageNavigation.Classify(uri));
}
