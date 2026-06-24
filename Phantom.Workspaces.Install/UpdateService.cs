using System.Security.Cryptography;

namespace Phantom.Workspaces.Install;

/// <summary>
/// Orchestrates checking for, downloading, verifying, staging, and applying updates over the
/// versioned-directory layout. It never mutates a running version's files: it stages a fresh
/// version directory and repoints <c>current</c>, retaining the previous version for rollback.
/// All seams (release source, downloader, extractor, filesystem) are injected so the orchestration
/// is fully unit-testable with no network, no real archive, and no real links.
/// </summary>
public sealed class UpdateService
{
    private readonly IReleaseSource releaseSource;
    private readonly IUpdateDownloader downloader;
    private readonly IArchiveExtractor extractor;
    private readonly IFileSystem fileSystem;
    private readonly InstallLayout layout;
    private readonly string runningVersion;
    private readonly string assetMoniker;

    /// <summary>
    /// Creates the service. <paramref name="runningVersion"/> is the running informational version
    /// and <paramref name="assetMoniker"/> selects the matching-architecture asset (e.g.
    /// <c>win-x64</c>).
    /// </summary>
    public UpdateService(
        IReleaseSource releaseSource,
        IUpdateDownloader downloader,
        IArchiveExtractor extractor,
        IFileSystem fileSystem,
        InstallLayout layout,
        string runningVersion,
        string assetMoniker)
    {
        ArgumentNullException.ThrowIfNull(releaseSource);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(runningVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetMoniker);

        this.releaseSource = releaseSource;
        this.downloader = downloader;
        this.extractor = extractor;
        this.fileSystem = fileSystem;
        this.layout = layout;
        this.runningVersion = runningVersion;
        this.assetMoniker = assetMoniker;
    }

    /// <summary>Raised when <see cref="CheckAsync"/> finds a newer release.</summary>
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    /// <summary>
    /// Queries the release source and reports whether a strictly newer stable release exists,
    /// raising <see cref="UpdateAvailable"/> when one does.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var releases = await this.releaseSource.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        var available = ReleaseSelector.SelectAvailableUpdate(releases, this.runningVersion);
        if (available is null)
        {
            return UpdateCheckResult.None;
        }

        this.UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(available));
        return new UpdateCheckResult { IsUpdateAvailable = true, LatestRelease = available };
    }

    /// <summary>
    /// Downloads the matching-architecture asset for <paramref name="release"/> into
    /// <c>updates\</c>, verifies its SHA256 (when published), and extracts it into a fresh
    /// <c>versions\&lt;version&gt;</c> directory. A hash mismatch is rejected and nothing is
    /// extracted or repointed, leaving <c>current</c> untouched. Returns the staged version name.
    /// </summary>
    public async Task<string> DownloadAndStageAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var version = NormalizeVersion(release.TagName);
        var asset = this.SelectAsset(release);

        this.fileSystem.CreateDirectory(this.layout.UpdatesRoot);
        var stagedArchive = Path.Combine(this.layout.UpdatesRoot, $"{version}.zip");
        await this.downloader.DownloadAsync(asset, stagedArchive, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(asset.Sha256))
        {
            var actual = this.ComputeSha256(stagedArchive);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                this.fileSystem.DeleteFile(stagedArchive);
                throw new UpdateVerificationException(
                    $"SHA256 mismatch for '{asset.Name}': expected {asset.Sha256}, got {actual}.");
            }
        }

        var versionDirectory = this.layout.GetVersionDirectory(version);
        if (this.fileSystem.DirectoryExists(versionDirectory))
        {
            this.fileSystem.DeleteDirectory(versionDirectory, recursive: true);
        }

        this.extractor.Extract(stagedArchive, versionDirectory);
        this.fileSystem.DeleteFile(stagedArchive);
        return version;
    }

    /// <summary>
    /// Applies a staged <paramref name="version"/> by repointing <c>current</c> at it and pruning
    /// superseded versions, retaining the previously-current version for rollback.
    /// </summary>
    public void Apply(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var previousVersion = this.layout.ResolveCurrentVersion();
        this.layout.RepointCurrent(version);
        this.layout.PruneVersions(keepVersion: version, alsoKeepVersion: previousVersion);
    }

    private ReleaseAsset SelectAsset(ReleaseInfo release)
    {
        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.Contains(this.assetMoniker, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return asset
            ?? throw new InvalidOperationException(
                $"Release '{release.TagName}' has no '{this.assetMoniker}' .zip asset.");
    }

    private string ComputeSha256(string path)
    {
        var bytes = this.fileSystem.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeVersion(string tag)
    {
        return SemanticVersion.TryParse(tag, out var version)
            ? version.ToString()
            : tag.TrimStart('v', 'V');
    }
}
