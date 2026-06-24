using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>An in-memory <see cref="IReleaseSource"/> serving canned releases.</summary>
public sealed class FakeReleaseSource : IReleaseSource
{
    private readonly IReadOnlyList<ReleaseInfo> releases;

    public FakeReleaseSource(params ReleaseInfo[] releases)
    {
        this.releases = releases;
    }

    public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(this.releases);
}

/// <summary>An <see cref="IUpdateDownloader"/> that writes canned bytes to the staged path.</summary>
public sealed class FakeUpdateDownloader : IUpdateDownloader
{
    private readonly IFileSystem fileSystem;
    private readonly byte[] payload;

    public FakeUpdateDownloader(IFileSystem fileSystem, byte[] payload)
    {
        this.fileSystem = fileSystem;
        this.payload = payload;
    }

    public List<string> DownloadedTo { get; } = new();

    public Task DownloadAsync(ReleaseAsset asset, string destinationPath, CancellationToken cancellationToken = default)
    {
        this.DownloadedTo.Add(destinationPath);
        this.fileSystem.WriteAllBytes(destinationPath, this.payload);
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IArchiveExtractor"/> that simulates extraction by writing a marker file.</summary>
public sealed class FakeArchiveExtractor : IArchiveExtractor
{
    private readonly IFileSystem fileSystem;

    public FakeArchiveExtractor(IFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    public List<string> ExtractedTo { get; } = new();

    public void Extract(string archivePath, string destinationDirectory)
    {
        this.ExtractedTo.Add(destinationDirectory);
        this.fileSystem.CreateDirectory(destinationDirectory);
        this.fileSystem.WriteAllText(
            Path.Combine(destinationDirectory, InstallLayout.ApplicationExecutableName),
            "payload");
    }
}
