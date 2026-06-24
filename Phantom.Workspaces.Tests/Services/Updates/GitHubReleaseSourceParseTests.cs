using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Tests.Updates;

public sealed class GitHubReleaseSourceParseTests
{
    [Fact]
    public void ParseReleases_ReadsArrayOfReleasesWithAssets()
    {
        const string json = """
        [
          {
            "tag_name": "v0.2.0",
            "draft": false,
            "prerelease": false,
            "assets": [
              { "name": "Phantom.Workspaces-win-x64.zip", "browser_download_url": "https://example/win-x64.zip" }
            ]
          },
          {
            "tag_name": "v0.1.0",
            "assets": []
          }
        ]
        """;

        var releases = GitHubReleaseSource.ParseReleases(json);

        Assert.Equal(2, releases.Count);
        Assert.Equal("v0.2.0", releases[0].TagName);
        var asset = Assert.Single(releases[0].Assets);
        Assert.Equal("Phantom.Workspaces-win-x64.zip", asset.Name);
        Assert.Equal("https://example/win-x64.zip", asset.DownloadUrl);
        Assert.Null(asset.Sha256);
        Assert.Empty(releases[1].Assets);
    }

    [Fact]
    public void ParseReleases_ReadsSingleReleaseObject()
    {
        const string json = """
        { "tag_name": "v1.0.0", "assets": [] }
        """;

        var releases = GitHubReleaseSource.ParseReleases(json);

        var release = Assert.Single(releases);
        Assert.Equal("v1.0.0", release.TagName);
    }

    [Fact]
    public void ParseReleases_HonoursInlineSha256AndDraftFlags()
    {
        const string json = """
        {
          "tag_name": "v2.0.0",
          "draft": true,
          "prerelease": true,
          "assets": [
            {
              "name": "Phantom.Workspaces-win-x64.zip",
              "browser_download_url": "https://example/a.zip",
              "sha256": "ABCDEF0123456789  Phantom.Workspaces-win-x64.zip"
            }
          ]
        }
        """;

        var release = Assert.Single(GitHubReleaseSource.ParseReleases(json));

        Assert.True(release.IsDraft);
        Assert.True(release.IsPrerelease);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("abcdef0123456789", asset.Sha256);
    }

    [Fact]
    public void ParseReleases_SkipsAssetsMissingNameOrUrl()
    {
        const string json = """
        {
          "tag_name": "v0.3.0",
          "assets": [
            { "browser_download_url": "https://example/no-name.zip" },
            { "name": "no-url.zip" },
            { "name": "good.zip", "browser_download_url": "https://example/good.zip" }
          ]
        }
        """;

        var release = Assert.Single(GitHubReleaseSource.ParseReleases(json));

        var asset = Assert.Single(release.Assets);
        Assert.Equal("good.zip", asset.Name);
    }
}
