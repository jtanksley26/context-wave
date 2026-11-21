using ContextWave.Core;

namespace ContextWave.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("2026.11.21+3635326abcdef0123456789abcdef0123456789", "2026.11.21+3635326")]
    [InlineData("2026.11.21+3635326-dirty", "2026.11.21+3635326-dirty")]
    [InlineData("2026.11.19-arch+3635326abcdef0123456789abcdef0123456789", "2026.11.19-arch+3635326")]
    public void A_packaged_build_reads_its_version_and_short_commit(string informational, string expected)
        => Assert.Equal(expected, AppVersion.Format(informational));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0-dev")]
    [InlineData("0.0.0-dev+3635326abcdef0123456789abcdef0123456789")]
    public void A_build_that_was_not_packaged_reads_dev(string? informational)
        => Assert.Equal("dev", AppVersion.Format(informational));

    [Fact]
    public void The_running_build_has_a_version() => Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
}
