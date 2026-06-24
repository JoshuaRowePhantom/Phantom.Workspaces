using System.Diagnostics;
using System.Runtime.Versioning;

namespace Phantom.Workspaces.Install;

/// <summary>
/// The production <see cref="IFileSystem"/> backed by <see cref="System.IO"/>. Directory links
/// are created as directory symbolic links, falling back to an NTFS junction on Windows when the
/// symbolic-link privilege is unavailable (junctions need no elevation/Developer Mode).
/// </summary>
public sealed class RealFileSystem : IFileSystem
{
    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        return Directory.EnumerateDirectories(path).ToArray();
    }

    /// <inheritdoc />
    public void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Combine(destinationDirectory, Relative(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Combine(destinationDirectory, Relative(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, contents);
    }

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void CreateOrReplaceDirectoryLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            // For a reparse point this removes the link itself, not the target's contents.
            Directory.Delete(linkPath);
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception)
            when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            CreateJunction(linkPath, targetPath);
        }
    }

    /// <inheritdoc />
    public string? ResolveDirectoryLinkTarget(string linkPath)
    {
        if (!Directory.Exists(linkPath))
        {
            return null;
        }

        return new DirectoryInfo(linkPath).LinkTarget;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Failed to start cmd.exe to create a junction.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"mklink /J failed (exit {process.ExitCode}) creating '{linkPath}' -> '{targetPath}'.");
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);

    private static string Combine(string root, string relative) => Path.Combine(root, relative);
}
