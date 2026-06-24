using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// A deterministic in-memory <see cref="IFileSystem"/> for unit tests. Directory links are
/// modelled as a separate link-path → target map so "repoint" and "resolve current" are
/// assertable without touching the real filesystem.
/// </summary>
internal sealed class InMemoryFileSystem : IFileSystem
{
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> links = new(StringComparer.OrdinalIgnoreCase);

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        return this.directories.Contains(normalized) || this.links.ContainsKey(normalized);
    }

    public bool FileExists(string path)
    {
        return this.files.ContainsKey(Normalize(path));
    }

    public void CreateDirectory(string path)
    {
        var normalized = Normalize(path);
        while (!string.IsNullOrEmpty(normalized))
        {
            if (!this.directories.Add(normalized))
            {
                break;
            }

            normalized = Path.GetDirectoryName(normalized) ?? string.Empty;
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        var normalized = Normalize(path);
        var prefix = normalized + Path.DirectorySeparatorChar;

        this.directories.RemoveWhere(directory =>
            string.Equals(directory, normalized, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        foreach (var file in this.files.Keys
                     .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            this.files.Remove(file);
        }

        foreach (var link in this.links.Keys
                     .Where(link => string.Equals(link, normalized, StringComparison.OrdinalIgnoreCase)
                                    || link.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            this.links.Remove(link);
        }
    }

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var normalized = Normalize(path);
        return this.directories
            .Where(directory => string.Equals(
                Path.GetDirectoryName(directory),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        var source = Normalize(sourceDirectory);
        var destination = Normalize(destinationDirectory);
        var sourcePrefix = source + Path.DirectorySeparatorChar;

        this.CreateDirectory(destination);
        foreach (var directory in this.directories
                     .Where(directory => directory.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            this.CreateDirectory(destination + directory[source.Length..]);
        }

        foreach (var (file, contents) in this.files
                     .Where(pair => pair.Key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            this.WriteAllText(destination + file[source.Length..], contents);
        }
    }

    public void WriteAllText(string path, string contents)
    {
        var normalized = Normalize(path);
        var parent = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(parent))
        {
            this.CreateDirectory(parent);
        }

        this.files[normalized] = contents;
    }

    public string ReadAllText(string path)
    {
        return this.files[Normalize(path)];
    }

    public void CreateOrReplaceDirectoryLink(string linkPath, string targetPath)
    {
        this.links[Normalize(linkPath)] = Normalize(targetPath);
    }

    public string? ResolveDirectoryLinkTarget(string linkPath)
    {
        return this.links.TryGetValue(Normalize(linkPath), out var target) ? target : null;
    }

    private static string Normalize(string path)
    {
        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }
}
