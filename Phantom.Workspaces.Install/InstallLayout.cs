namespace Phantom.Workspaces.Install;

/// <summary>
/// The on-disk install layout shared by the installer and the in-app updater:
/// <c>app\current</c> (a directory link), <c>app\versions\&lt;v&gt;</c>, and <c>app\updates</c>.
/// Updating is "drop a new version folder and repoint the link", and shortcuts/alias/startup
/// task all reference the stable <c>current</c> path so they survive version changes.
/// </summary>
public sealed class InstallLayout
{
    /// <summary>The shipped GUI executable name.</summary>
    public const string ApplicationExecutableName = "Phantom.Workspaces.exe";

    /// <summary>The stable <c>current</c> link name.</summary>
    public const string CurrentLinkName = "current";

    /// <summary>The directory holding versioned payloads.</summary>
    public const string VersionsDirectoryName = "versions";

    /// <summary>The scratch directory for in-progress downloads.</summary>
    public const string UpdatesDirectoryName = "updates";

    /// <summary>The install marker/metadata file name.</summary>
    public const string InstallMetadataFileName = ".install-metadata.json";

    private readonly IFileSystem fileSystem;

    /// <summary>Creates a layout rooted at <paramref name="appRoot"/> over <paramref name="fileSystem"/>.</summary>
    public InstallLayout(IFileSystem fileSystem, string appRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);
        this.fileSystem = fileSystem;
        this.AppRoot = appRoot;
    }

    /// <summary>The managed <c>app\</c> root.</summary>
    public string AppRoot { get; }

    /// <summary>The <c>app\current</c> link path.</summary>
    public string CurrentLinkPath => Path.Combine(this.AppRoot, CurrentLinkName);

    /// <summary>The <c>app\versions</c> directory.</summary>
    public string VersionsRoot => Path.Combine(this.AppRoot, VersionsDirectoryName);

    /// <summary>The <c>app\updates</c> directory.</summary>
    public string UpdatesRoot => Path.Combine(this.AppRoot, UpdatesDirectoryName);

    /// <summary>The stable <c>app\current\Phantom.Workspaces.exe</c> path.</summary>
    public string CurrentExecutablePath => Path.Combine(this.CurrentLinkPath, ApplicationExecutableName);

    /// <summary>The install metadata path under the managed root.</summary>
    public string InstallMetadataPath => Path.Combine(this.AppRoot, InstallMetadataFileName);

    /// <summary>The directory for the given <paramref name="version"/>.</summary>
    public string GetVersionDirectory(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Path.Combine(this.VersionsRoot, version);
    }

    /// <summary>The executable path for the given <paramref name="version"/>.</summary>
    public string GetVersionExecutablePath(string version)
    {
        return Path.Combine(this.GetVersionDirectory(version), ApplicationExecutableName);
    }

    /// <summary>Returns whether <paramref name="executablePath"/> lives under the managed versions tree.</summary>
    public bool IsManagedExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var normalizedVersions = NormalizeForComparison(this.VersionsRoot);
        var normalizedExecutable = NormalizeForComparison(executablePath);
        return normalizedExecutable.StartsWith(
            normalizedVersions + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The version names currently present under <c>versions\</c>.</summary>
    public IReadOnlyList<string> GetInstalledVersions()
    {
        if (!this.fileSystem.DirectoryExists(this.VersionsRoot))
        {
            return Array.Empty<string>();
        }

        return this.fileSystem
            .EnumerateDirectories(this.VersionsRoot)
            .Select(static directory => Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(static name => !string.IsNullOrEmpty(name))
            .ToArray();
    }

    /// <summary>
    /// Atomically repoints <c>current</c> at the directory for <paramref name="version"/>. The
    /// version directory must already exist.
    /// </summary>
    public void RepointCurrent(string version)
    {
        var versionDirectory = this.GetVersionDirectory(version);
        if (!this.fileSystem.DirectoryExists(versionDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Cannot repoint current to missing version directory '{versionDirectory}'.");
        }

        this.fileSystem.CreateDirectory(this.AppRoot);
        this.fileSystem.CreateOrReplaceDirectoryLink(this.CurrentLinkPath, versionDirectory);
    }

    /// <summary>The version <c>current</c> currently resolves to, or <c>null</c> when unset.</summary>
    public string? ResolveCurrentVersion()
    {
        var target = this.fileSystem.ResolveDirectoryLinkTarget(this.CurrentLinkPath);
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        var name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Bootstraps <paramref name="payloadDirectory"/> (an already-extracted published payload)
    /// into <c>versions\&lt;version&gt;</c>, repoints <c>current</c>, and writes the install
    /// marker. Idempotent: re-running repairs the link and marker without recopying.
    /// </summary>
    public void Bootstrap(string payloadDirectory, string version, DateTimeOffset installedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        var versionDirectory = this.GetVersionDirectory(version);

        this.fileSystem.CreateDirectory(this.VersionsRoot);
        this.fileSystem.CreateDirectory(this.UpdatesRoot);
        if (!this.fileSystem.DirectoryExists(versionDirectory))
        {
            this.fileSystem.CopyDirectory(payloadDirectory, versionDirectory);
        }

        this.RepointCurrent(version);
        this.WriteInstallMetadata(new InstallMetadata
        {
            Version = version,
            InstalledAtUtc = installedAtUtc,
        });
    }

    /// <summary>Writes <paramref name="metadata"/> to the managed marker file.</summary>
    public void WriteInstallMetadata(InstallMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        this.fileSystem.CreateDirectory(this.AppRoot);
        this.fileSystem.WriteAllText(this.InstallMetadataPath, metadata.ToJson());
    }

    /// <summary>Reads the managed install marker, or <c>null</c> when absent/invalid.</summary>
    public InstallMetadata? ReadInstallMetadata()
    {
        if (!this.fileSystem.FileExists(this.InstallMetadataPath))
        {
            return null;
        }

        return InstallMetadata.FromJson(this.fileSystem.ReadAllText(this.InstallMetadataPath));
    }

    /// <summary>
    /// Prunes superseded version directories, retaining <paramref name="keepVersion"/> and the
    /// optional <paramref name="alsoKeepVersion"/> (the previous version held for rollback).
    /// </summary>
    public void PruneVersions(string keepVersion, string? alsoKeepVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keepVersion);
        foreach (var version in this.GetInstalledVersions())
        {
            if (string.Equals(version, keepVersion, StringComparison.OrdinalIgnoreCase)
                || (alsoKeepVersion is not null
                    && string.Equals(version, alsoKeepVersion, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            this.fileSystem.DeleteDirectory(this.GetVersionDirectory(version), recursive: true);
        }
    }

    private static string NormalizeForComparison(string path)
    {
        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }
}
