using System.Security.Cryptography;
using System.Text;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class UpdateServiceTests
{
    private const string AppRoot = @"C:\sandbox\app";
    private const string AssetMoniker = "win-x64";
    private static readonly DateTimeOffset Installed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("zip-bytes");

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ReleaseInfo ReleaseWithAsset(string tag, string? sha256)
    {
        return new ReleaseInfo
        {
            TagName = tag,
            Assets = new[]
            {
                new ReleaseAsset
                {
                    Name = $"Phantom.Workspaces-{AssetMoniker}.zip",
                    DownloadUrl = $"https://example.test/{tag}/Phantom.Workspaces-{AssetMoniker}.zip",
                    Sha256 = sha256,
                },
            },
        };
    }

    private static (InMemoryFileSystem FileSystem, InstallLayout Layout) NewLayoutWithCurrent(string currentVersion)
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        fileSystem.CreateDirectory(layout.GetVersionDirectory(currentVersion));
        layout.RepointCurrent(currentVersion);
        return (fileSystem, layout);
    }

    private static UpdateService NewService(
        InstallLayout layout,
        IFileSystem fileSystem,
        IReleaseSource releaseSource,
        IUpdateDownloader downloader,
        IArchiveExtractor extractor,
        string runningVersion)
    {
        return new UpdateService(releaseSource, downloader, extractor, fileSystem, layout, runningVersion, AssetMoniker);
    }

    [Fact]
    public async Task CheckAsync_ReportsAvailableAndRaisesEventWhenNewer()
    {
        var (fileSystem, layout) = NewLayoutWithCurrent("0.1.0");
        var releaseSource = new FakeReleaseSource(ReleaseWithAsset("v0.2.0", null));
        var service = NewService(
            layout,
            fileSystem,
            releaseSource,
            new FakeUpdateDownloader(fileSystem, Payload),
            new FakeArchiveExtractor(fileSystem),
            "0.1.0");

        ReleaseInfo? raised = null;
        service.UpdateAvailable += (_, args) => raised = args.Release;

        var result = await service.CheckAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("v0.2.0", result.LatestRelease?.TagName);
        Assert.Equal("v0.2.0", raised?.TagName);
    }

    [Fact]
    public async Task CheckAsync_ReportsNoneWhenNotNewer()
    {
        var (fileSystem, layout) = NewLayoutWithCurrent("0.2.0");
        var releaseSource = new FakeReleaseSource(ReleaseWithAsset("v0.2.0", null));
        var service = NewService(
            layout,
            fileSystem,
            releaseSource,
            new FakeUpdateDownloader(fileSystem, Payload),
            new FakeArchiveExtractor(fileSystem),
            "0.2.0");

        var raised = false;
        service.UpdateAvailable += (_, _) => raised = true;

        var result = await service.CheckAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.False(raised);
    }

    [Fact]
    public async Task DownloadAndStageAsync_VerifiesHashAndExtractsNewVersion()
    {
        var (fileSystem, layout) = NewLayoutWithCurrent("0.1.0");
        var release = ReleaseWithAsset("v0.2.0", Sha256Hex(Payload));
        var service = NewService(
            layout,
            fileSystem,
            new FakeReleaseSource(release),
            new FakeUpdateDownloader(fileSystem, Payload),
            new FakeArchiveExtractor(fileSystem),
            "0.1.0");

        var staged = await service.DownloadAndStageAsync(release);

        Assert.Equal("0.2.0", staged);
        Assert.True(fileSystem.FileExists(layout.GetVersionExecutablePath("0.2.0")));
        // The staged archive is cleaned up and current is not yet moved.
        Assert.False(fileSystem.FileExists(Path.Combine(layout.UpdatesRoot, "0.2.0.zip")));
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
    }

    [Fact]
    public async Task DownloadAndStageAsync_RejectsHashMismatchAndLeavesCurrentUntouched()
    {
        var (fileSystem, layout) = NewLayoutWithCurrent("0.1.0");
        var release = ReleaseWithAsset("v0.2.0", Sha256Hex(Encoding.UTF8.GetBytes("different")));
        var service = NewService(
            layout,
            fileSystem,
            new FakeReleaseSource(release),
            new FakeUpdateDownloader(fileSystem, Payload),
            new FakeArchiveExtractor(fileSystem),
            "0.1.0");

        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.DownloadAndStageAsync(release));

        Assert.False(fileSystem.DirectoryExists(layout.GetVersionDirectory("0.2.0")));
        Assert.False(fileSystem.FileExists(Path.Combine(layout.UpdatesRoot, "0.2.0.zip")));
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
    }

    [Fact]
    public void Apply_RepointsCurrentAndRetainsPreviousForRollback()
    {
        var (fileSystem, layout) = NewLayoutWithCurrent("0.1.0");
        // A superseded version that should be pruned, and the new staged version.
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.0.9"));
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.2.0"));
        var service = NewService(
            layout,
            fileSystem,
            new FakeReleaseSource(),
            new FakeUpdateDownloader(fileSystem, Payload),
            new FakeArchiveExtractor(fileSystem),
            "0.1.0");

        service.Apply("0.2.0");

        Assert.Equal("0.2.0", layout.ResolveCurrentVersion());
        var versions = layout.GetInstalledVersions();
        Assert.Contains("0.2.0", versions);
        Assert.Contains("0.1.0", versions);
        Assert.DoesNotContain("0.0.9", versions);
    }
}
