using System.IO.Compression;

namespace Phantom.Workspaces.Install;

/// <summary>The production <see cref="IUpdateDownloader"/> backed by <see cref="HttpClient"/>.</summary>
public sealed class HttpUpdateDownloader : IUpdateDownloader
{
    private readonly HttpClient httpClient;

    /// <summary>Creates the downloader over <paramref name="httpClient"/>.</summary>
    public HttpUpdateDownloader(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        ReleaseAsset asset,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var response = await this.httpClient
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The production <see cref="IArchiveExtractor"/> backed by <see cref="ZipFile"/>.</summary>
public sealed class ZipArchiveExtractor : IArchiveExtractor
{
    /// <inheritdoc />
    public void Extract(string archivePath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
    }
}
