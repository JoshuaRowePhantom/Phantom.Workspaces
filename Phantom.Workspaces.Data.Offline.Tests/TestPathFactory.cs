using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Offline.Tests;

internal static class TestPathFactory
{
    public static string CreateIsolatedDirectory(
        string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Test",
            name,
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static TempDirectory CreateIsolatedTempDirectory(
        string name)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Test",
            name);
        Directory.CreateDirectory(parent);
        return TempDirectory.CreateInside(parent, prefix: string.Empty);
    }
}

