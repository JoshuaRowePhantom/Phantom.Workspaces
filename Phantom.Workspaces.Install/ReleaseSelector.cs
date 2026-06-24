namespace Phantom.Workspaces.Install;

/// <summary>
/// Selects the newest published, non-draft, non-pre-release entry from a set of releases and
/// decides whether it is strictly newer than the running version. Releases with unparseable tags
/// are ignored.
/// </summary>
public static class ReleaseSelector
{
    /// <summary>
    /// Returns the newest stable release (highest <see cref="SemanticVersion"/> tag, ignoring
    /// drafts, pre-releases, and unparseable tags), or <c>null</c> when none qualify.
    /// </summary>
    public static ReleaseInfo? SelectLatestStable(IEnumerable<ReleaseInfo> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);

        ReleaseInfo? best = null;
        SemanticVersion bestVersion = default;
        foreach (var release in releases)
        {
            if (release.IsDraft || release.IsPrerelease)
            {
                continue;
            }

            if (!SemanticVersion.TryParse(release.TagName, out var version) || version.IsPrerelease)
            {
                continue;
            }

            if (best is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the latest stable release when it is strictly newer than
    /// <paramref name="currentVersion"/>, otherwise <c>null</c>. An unparseable current version is
    /// treated as the lowest possible version so any valid release counts as an update.
    /// </summary>
    public static ReleaseInfo? SelectAvailableUpdate(IEnumerable<ReleaseInfo> releases, string currentVersion)
    {
        var latest = SelectLatestStable(releases);
        if (latest is null)
        {
            return null;
        }

        if (!SemanticVersion.TryParse(latest.TagName, out var latestVersion))
        {
            return null;
        }

        if (SemanticVersion.TryParse(currentVersion, out var current) && latestVersion <= current)
        {
            return null;
        }

        return latest;
    }
}
