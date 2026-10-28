namespace Phantom.Workspaces.Tools;

public enum GitWorkspaceAvailability
{
    Unknown,
    Exists,
    ConfirmedMissing,
}

internal static class GitWorkspacePath
{
    public static bool IsRecycled(string path)
        => Path.GetFullPath(path).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(component => string.Equals(component, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase));

    public static GitWorkspaceAvailability Classify(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            try
            {
                return (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0
                    ? GitWorkspaceAvailability.Exists
                    : GitWorkspaceAvailability.Unknown;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A failed Exists probe is not deletion evidence. Walk from a readable root and
                // require successful enumeration of the immediate parent of the missing component.
                var current = Path.GetPathRoot(fullPath)!;
                foreach (var component in fullPath[current.Length..].Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var entries = Directory.GetFileSystemEntries(current);
                    var candidate = Path.Combine(current, component);
                    if (!entries.Any(entry => string.Equals(entry, candidate, StringComparison.OrdinalIgnoreCase)))
                    {
                        return GitWorkspaceAvailability.ConfirmedMissing;
                    }

                    current = candidate;
                }

                return GitWorkspaceAvailability.Unknown;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException
                                          or System.Security.SecurityException)
        {
            return GitWorkspaceAvailability.Unknown;
        }
    }
}
