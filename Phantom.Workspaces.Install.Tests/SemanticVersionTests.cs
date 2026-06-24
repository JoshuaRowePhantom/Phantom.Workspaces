using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V0.2.0", 0, 2, 0, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1", 1, 0, 0, null)]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("v1.2.3-rc1+build.7", 1, 2, 3, "rc1")]
    public void TryParse_ParsesValidVersions(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    public void TryParse_RejectsInvalidVersions(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void CompareTo_OrdersByCoreComponents()
    {
        Assert.True(Parse("1.0.0") < Parse("1.0.1"));
        Assert.True(Parse("1.1.0") > Parse("1.0.9"));
        Assert.True(Parse("2.0.0") > Parse("1.9.9"));
        Assert.Equal(0, Parse("1.2.3").CompareTo(Parse("1.2.3")));
    }

    [Fact]
    public void CompareTo_TreatsPrereleaseAsLowerThanRelease()
    {
        Assert.True(Parse("1.2.3-rc1") < Parse("1.2.3"));
        Assert.True(Parse("1.2.3") > Parse("1.2.3-rc1"));
        Assert.True(Parse("1.2.3-alpha") < Parse("1.2.3-beta"));
    }

    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }
}
