namespace Phantom.Workspaces.Install;

/// <summary>A downloadable asset attached to a release.</summary>
public sealed record ReleaseAsset
{
    /// <summary>The asset file name (e.g. <c>Phantom.Workspaces-win-x64.zip</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The HTTPS download URL.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>The published SHA256 hash (lower-case hex), when known.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>A release as reported by the release source (e.g. a GitHub Release).</summary>
public sealed record ReleaseInfo
{
    /// <summary>The release tag (e.g. <c>v0.2.0</c>).</summary>
    public required string TagName { get; init; }

    /// <summary>Whether the release is a draft.</summary>
    public bool IsDraft { get; init; }

    /// <summary>Whether the release is flagged as a pre-release.</summary>
    public bool IsPrerelease { get; init; }

    /// <summary>The assets attached to the release.</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = Array.Empty<ReleaseAsset>();
}

/// <summary>
/// Wraps the GitHub Releases API so update checks are deterministic and network-free in tests.
/// Implementations resolve the feed, with a <c>PHANTOM_WORKSPACES_UPDATE_FEED</c> override so the
/// updater can be pointed at a local fake release.
/// </summary>
public interface IReleaseSource
{
    /// <summary>Returns all releases known to the source (newest-first ordering is not assumed).</summary>
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken = default);
}
