using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

// The whole feature is one judgement: is the released version newer than this one. Get that wrong
// in either direction and it is either a banner that never appears or one that never goes away.
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v2.2.0", "2.2.0")]
    [InlineData("2.2.0", "2.2.0")]
    [InlineData("V2.2.0", "2.2.0")]
    [InlineData("v2.3.0-beta", "2.3.0")]
    [InlineData("v2.3.0+build7", "2.3.0")]
    [InlineData("  v2.2.0  ", "2.2.0")]
    public void A_release_tag_becomes_a_plain_version(string tag, string expected)
        => Assert.Equal(expected, UpdateChecker.Normalize(tag));

    [Theory]
    [InlineData("2.3.0", "2.2.0")]
    [InlineData("3.0.0", "2.9.9")]
    [InlineData("2.2.1", "2.2.0")]
    [InlineData("2.2", "2.1.9")]
    public void A_higher_release_is_offered(string latest, string current)
        => Assert.True(UpdateChecker.IsNewer(latest, current));

    [Theory]
    [InlineData("2.2.0", "2.2.0")]   // same build
    [InlineData("2.1.0", "2.2.0")]   // older release than what is installed
    [InlineData("2.2.0", "2.2.1")]
    public void The_same_or_an_older_release_is_not_offered(string latest, string current)
        => Assert.False(UpdateChecker.IsNewer(latest, current));

    [Fact]
    public void Ten_is_newer_than_nine()
    {
        // The bug a string comparison always produces: "2.10.0" sorts BELOW "2.9.0" as text, so the
        // update that matters most - the one after nine releases - is the one that never appears.
        Assert.True(UpdateChecker.IsNewer("2.10.0", "2.9.0"));
        Assert.False(UpdateChecker.IsNewer("2.9.0", "2.10.0"));
    }

    [Theory]
    [InlineData("not-a-version", "2.2.0")]
    [InlineData("", "2.2.0")]
    [InlineData("2.x.0", "2.2.0")]
    public void An_unreadable_tag_offers_nothing(string latest, string current)
    {
        // Silence beats a wrong "update available", which sends someone to reinstall what they have.
        Assert.False(UpdateChecker.IsNewer(latest, current));
    }

    [Fact]
    public void The_build_reports_a_real_version()
    {
        // Guards the csproj <Version>: without it the assembly reports 1.0.0, every release looks
        // newer, and a fresh install nags forever.
        Assert.NotEqual("0.0.0", UpdateChecker.CurrentVersion);
        Assert.NotEqual("1.0.0", UpdateChecker.CurrentVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+$", UpdateChecker.CurrentVersion);
    }

    [Fact]
    public void Sellers_are_sent_to_the_download_page_not_the_git_tag()
    {
        // A tag exists the moment it is pushed; the installer exists only once it has been shipped.
        Assert.DoesNotContain("github", UpdateChecker.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", UpdateChecker.DownloadUrl);
    }
}
