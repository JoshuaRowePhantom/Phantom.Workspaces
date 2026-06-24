using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class ReleaseSelectorTests
{
    private static ReleaseInfo Release(string tag, bool draft = false, bool prerelease = false)
    {
        return new ReleaseInfo { TagName = tag, IsDraft = draft, IsPrerelease = prerelease };
    }

    [Fact]
    public void SelectLatestStable_PicksHighestStableTag()
    {
        var releases = new[]
        {
            Release("v0.1.0"),
            Release("v0.3.0"),
            Release("v0.2.0"),
        };

        Assert.Equal("v0.3.0", ReleaseSelector.SelectLatestStable(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStable_IgnoresDraftsPrereleasesAndUnparseable()
    {
        var releases = new[]
        {
            Release("v0.4.0", draft: true),
            Release("v0.5.0", prerelease: true),
            Release("v0.3.0-rc1"),
            Release("nightly"),
            Release("v0.2.0"),
        };

        Assert.Equal("v0.2.0", ReleaseSelector.SelectLatestStable(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStable_ReturnsNullWhenNoneQualify()
    {
        var releases = new[] { Release("v1.0.0", draft: true), Release("v0.9.0", prerelease: true) };
        Assert.Null(ReleaseSelector.SelectLatestStable(releases));
    }

    [Fact]
    public void SelectAvailableUpdate_ReturnsReleaseWhenStrictlyNewer()
    {
        var releases = new[] { Release("v0.2.0"), Release("v0.3.0") };
        Assert.Equal("v0.3.0", ReleaseSelector.SelectAvailableUpdate(releases, "0.2.0")?.TagName);
    }

    [Fact]
    public void SelectAvailableUpdate_ReturnsNullWhenNotNewer()
    {
        var releases = new[] { Release("v0.2.0") };
        Assert.Null(ReleaseSelector.SelectAvailableUpdate(releases, "0.2.0"));
        Assert.Null(ReleaseSelector.SelectAvailableUpdate(releases, "0.3.0"));
    }

    [Fact]
    public void SelectAvailableUpdate_TreatsUnparseableCurrentVersionAsOldest()
    {
        var releases = new[] { Release("v0.1.0") };
        Assert.Equal("v0.1.0", ReleaseSelector.SelectAvailableUpdate(releases, "unknown")?.TagName);
    }
}
