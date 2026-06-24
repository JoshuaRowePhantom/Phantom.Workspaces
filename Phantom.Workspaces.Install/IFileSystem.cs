namespace Phantom.Workspaces.Install;

/// <summary>
/// Filesystem operations used by <see cref="InstallLayout"/>, bootstrap, and the updater. The
/// seam lets the install/update logic run against an in-memory fake in unit tests; the
/// <c>current</c> directory link is modelled as an indirection the fake can represent so
/// "repoint" and "resolve <c>current</c>" are assertable without real symlinks/junctions.
/// </summary>
public interface IFileSystem
{
    /// <summary>Returns whether a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Returns whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Creates the directory (and parents) at <paramref name="path"/> if absent.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes the directory at <paramref name="path"/>, recursively when requested.</summary>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>Lists the immediate child directories of <paramref name="path"/>.</summary>
    IReadOnlyList<string> EnumerateDirectories(string path);

    /// <summary>Recursively copies <paramref name="sourceDirectory"/> to <paramref name="destinationDirectory"/>.</summary>
    void CopyDirectory(string sourceDirectory, string destinationDirectory);

    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/>, overwriting.</summary>
    void WriteAllText(string path, string contents);

    /// <summary>Reads all text from <paramref name="path"/>.</summary>
    string ReadAllText(string path);

    /// <summary>
    /// Creates (or atomically replaces) a directory link at <paramref name="linkPath"/> pointing
    /// at <paramref name="targetPath"/>. Real implementations prefer an NTFS junction (no
    /// elevation) and fall back as needed.
    /// </summary>
    void CreateOrReplaceDirectoryLink(string linkPath, string targetPath);

    /// <summary>
    /// Resolves the target of the directory link at <paramref name="linkPath"/>, or <c>null</c>
    /// when the path is absent or is not a link.
    /// </summary>
    string? ResolveDirectoryLinkTarget(string linkPath);
}
