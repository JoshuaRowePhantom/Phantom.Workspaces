namespace Phantom.Workspaces.Llm.Copilot;

internal static class CopilotPathSecurity
{
    internal static void EnsureNoReparsePoints(string path, string? trustedRoot = null)
    {
        var canonicalPath = Path.GetFullPath(path);
        var root = trustedRoot is null
            ? Path.GetPathRoot(canonicalPath)
                ?? throw new ArgumentException("The path has no filesystem root.", nameof(path))
            : Path.GetFullPath(trustedRoot);
        var relativePath = Path.GetRelativePath(root, canonicalPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The path is outside its trusted root.");
        }

        EnsureNotReparsePoint(root);
        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The path traverses a reparse point.");
    }
}
