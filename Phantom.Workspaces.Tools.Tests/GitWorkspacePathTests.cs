using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitWorkspacePathTests
{
    [Theory]
    [InlineData(@"C:\$Recycle.Bin", true)]
    [InlineData(@"C:\$RECYCLE.BIN\user\repository\", true)]
    [InlineData(@"C:/$Recycle.Bin/user/repository", true)]
    [InlineData(@"C:\$Recycle.Bin-backup\repository", false)]
    [InlineData(@"C:\ordinary\repository", false)]
    public void IsRecycled_ComponentBoundaries_AreMandatory(string path, bool expected)
        => Assert.Equal(expected, GitWorkspacePath.IsRecycled(path));

    [Fact]
    public void Classify_AccessibleExistingAndMissingDirectories_DistinguishesAvailability()
    {
        using var root = new TempDirectory("workspace-availability-");
        Assert.Equal(GitWorkspaceAvailability.Exists, GitWorkspacePath.Classify(root.Path));
        Assert.Equal(GitWorkspaceAvailability.ConfirmedMissing,
            GitWorkspacePath.Classify(Path.Combine(root.Path, "missing", "nested")));
        Assert.Equal(GitWorkspaceAvailability.Unknown, GitWorkspacePath.Classify("\0"));
        var file = Path.Combine(root.Path, "file");
        File.WriteAllText(file, "not a directory");
        Assert.Equal(GitWorkspaceAvailability.Unknown, GitWorkspacePath.Classify(file));
        Assert.Equal(GitWorkspaceAvailability.Unknown, GitWorkspacePath.Classify(Path.Combine(file, "nested")));
    }
}
