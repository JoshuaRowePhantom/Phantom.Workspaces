using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>
/// The production <see cref="IReleaseSource"/> backed by the GitHub Releases API. The feed is
/// overridable through the <c>PHANTOM_WORKSPACES_UPDATE_FEED</c> environment variable (a path to a
/// local JSON file shaped like the GitHub <c>/releases</c> response), so the updater can be pointed
/// at a local fake release for end-to-end testing without hitting the network.
/// </summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    /// <summary>The environment variable that overrides the release feed with a local JSON file.</summary>
    public const string FeedOverrideVariable = "PHANTOM_WORKSPACES_UPDATE_FEED";

    /// <summary>The default repository slug releases are read from.</summary>
    public const string DefaultRepositorySlug = "JoshuaRowePhantom/Phantom.Workspaces";

    private readonly HttpClient httpClient;
    private readonly string repositorySlug;
    private readonly string? feedOverride;

    /// <summary>
    /// Creates the source. <paramref name="feedOverride"/> defaults to the
    /// <see cref="FeedOverrideVariable"/> environment variable when not supplied.
    /// </summary>
    public GitHubReleaseSource(
        HttpClient httpClient,
        string repositorySlug = DefaultRepositorySlug,
        string? feedOverride = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositorySlug);
        this.httpClient = httpClient;
        this.repositorySlug = repositorySlug;
        this.feedOverride = feedOverride ?? Environment.GetEnvironmentVariable(FeedOverrideVariable);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        var json = await this.ReadFeedAsync(cancellationToken).ConfigureAwait(false);
        var releases = ParseReleases(json);
        var resolved = new List<ReleaseInfo>(releases.Count);
        foreach (var release in releases)
        {
            resolved.Add(await this.ResolveCompanionChecksumsAsync(release, cancellationToken).ConfigureAwait(false));
        }

        return resolved;
    }

    /// <summary>
    /// Parses a GitHub <c>/releases</c> JSON payload (an array of releases, or a single release
    /// object) into <see cref="ReleaseInfo"/> records. An optional <c>sha256</c> property on an
    /// asset is honoured so a fake feed can publish checksums inline.
    /// </summary>
    internal static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var releases = new List<ReleaseInfo>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                releases.Add(ParseRelease(element));
            }
        }
        else
        {
            releases.Add(ParseRelease(root));
        }

        return releases;
    }

    private static ReleaseInfo ParseRelease(JsonElement element)
    {
        var tagName = element.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()!
            : throw new FormatException("A release entry is missing its 'tag_name'.");

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetArray.EnumerateArray())
            {
                var name = assetElement.TryGetProperty("name", out var nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()!
                        : null;
                var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement)
                    && urlElement.ValueKind == JsonValueKind.String
                        ? urlElement.GetString()!
                        : null;
                if (name is null || downloadUrl is null)
                {
                    continue;
                }

                var sha256 = assetElement.TryGetProperty("sha256", out var shaElement)
                    && shaElement.ValueKind == JsonValueKind.String
                        ? NormalizeChecksum(shaElement.GetString())
                        : null;

                assets.Add(new ReleaseAsset { Name = name, DownloadUrl = downloadUrl, Sha256 = sha256 });
            }
        }

        return new ReleaseInfo
        {
            TagName = tagName,
            IsDraft = element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            IsPrerelease = element.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True,
            Assets = assets,
        };
    }

    private async Task<string> ReadFeedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(this.feedOverride) && File.Exists(this.feedOverride))
        {
            return await File.ReadAllTextAsync(this.feedOverride, cancellationToken).ConfigureAwait(false);
        }

        var url = !string.IsNullOrWhiteSpace(this.feedOverride)
            ? this.feedOverride!
            : $"https://api.github.com/repos/{this.repositorySlug}/releases?per_page=10";
        return await this.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReleaseInfo> ResolveCompanionChecksumsAsync(
        ReleaseInfo release,
        CancellationToken cancellationToken)
    {
        var assetsByName = release.Assets.ToDictionary(asset => asset.Name, StringComparer.OrdinalIgnoreCase);
        var resolvedAssets = new List<ReleaseAsset>(release.Assets.Count);
        foreach (var asset in release.Assets)
        {
            if (IsZipNeedingChecksum(asset)
                && assetsByName.TryGetValue($"{asset.Name}.sha256", out var checksumAsset))
            {
                var content = await this.ReadTextAsync(checksumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
                var checksum = NormalizeChecksum(content);
                resolvedAssets.Add(checksum is null ? asset : asset with { Sha256 = checksum });
            }
            else
            {
                resolvedAssets.Add(asset);
            }
        }

        return release with { Assets = resolvedAssets };
    }

    private static bool IsZipNeedingChecksum(ReleaseAsset asset)
        => string.IsNullOrEmpty(asset.Sha256)
            && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private async Task<string> ReadTextAsync(string urlOrPath, CancellationToken cancellationToken)
    {
        if (File.Exists(urlOrPath))
        {
            return await File.ReadAllTextAsync(urlOrPath, cancellationToken).ConfigureAwait(false);
        }

        return await this.GetStringAsync(urlOrPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("phantom-workspaces-updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeChecksum(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // ".sha256" files use the "<hash>  <filename>" shape; take the leading hash token.
        var token = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return token.ToLower(CultureInfo.InvariantCulture);
    }
}
