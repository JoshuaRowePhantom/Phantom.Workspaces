namespace Phantom.Workspaces.Install;

/// <summary>
/// Downloads a release asset to a local path. The seam keeps <see cref="UpdateService"/> testable
/// with no real network: a fake serves canned bytes.
/// </summary>
public interface IUpdateDownloader
{
    /// <summary>Downloads <paramref name="asset"/> to <paramref name="destinationPath"/>.</summary>
    Task DownloadAsync(ReleaseAsset asset, string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extracts a downloaded archive into a version directory. The seam keeps extraction testable
/// without a real zip implementation in unit tests.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>Extracts <paramref name="archivePath"/> into <paramref name="destinationDirectory"/>.</summary>
    void Extract(string archivePath, string destinationDirectory);
}
