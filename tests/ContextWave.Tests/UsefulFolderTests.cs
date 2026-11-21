using ContextWave.Core;

namespace ContextWave.Tests;

public class UsefulFolderTests
{
    private const string Windows = @"C:\Windows";

    [Theory]
    [InlineData(@"C:\Windows", null)]
    [InlineData(@"C:\Windows\System32", null)]
    [InlineData(@"c:\windows\system32\", null)]
    [InlineData("C:/Windows/System32", null)]
    // Padding is ignored when judging the folder, so a padded System32 is still unusable.
    [InlineData(@" C:\Windows\System32 ", null)]
    [InlineData(@"C:\Windows.old\x", @"C:\Windows.old\x")]
    [InlineData(@"C:\WindowsTools", @"C:\WindowsTools")]
    [InlineData(@"D:\Projects\md5reader", @"D:\Projects\md5reader")]
    [InlineData("  ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_a_folder_outside_the_windows_folder_is_useful(string? folder, string? expected)
        => Assert.Equal(expected, ChatTabs.UsefulFolder(folder, Windows));

    [Fact]
    public void Every_folder_is_useful_when_the_windows_folder_is_unknown()
        => Assert.Equal(@"C:\Windows", ChatTabs.UsefulFolder(@"C:\Windows", ""));
}
