using System;
using System.IO;
using LibGit2Sharp;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class TempDirectoryTests
{
    [Fact]
    public void TempDirectory_WhenDisposed_DeletesDirectoryFromDisk()
    {
        string path;
        using (var temp = new TempDirectory("pw-tests-disposed-"))
        {
            path = temp.Path;
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void TempDirectory_WhenContainsReadOnlyFiles_DisposeStillDeletes()
    {
        string path;
        using (var temp = new TempDirectory("pw-tests-readonly-"))
        {
            path = temp.Path;
            var filePath = Path.Combine(path, "readonly.bin");
            File.WriteAllText(filePath, "content");
            File.SetAttributes(filePath, FileAttributes.ReadOnly);

            var nestedDir = Path.Combine(path, "nested");
            Directory.CreateDirectory(nestedDir);
            var nestedFilePath = Path.Combine(nestedDir, "also-readonly.bin");
            File.WriteAllText(nestedFilePath, "content");
            File.SetAttributes(nestedFilePath, FileAttributes.ReadOnly);
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void TempDirectory_WhenContainsInitializedGitRepo_DisposeStillDeletes()
    {
        string path;
        using (var temp = new TempDirectory("pw-tests-git-repo-"))
        {
            path = temp.Path;
            Repository.Init(path);
            Assert.True(Directory.Exists(Path.Combine(path, ".git")));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void TempDirectory_WhenTestBodyThrows_StillDeletesDirectory()
    {
        string capturedPath = string.Empty;

        Action act = () =>
        {
            using var temp = new TempDirectory("pw-tests-throws-");
            capturedPath = temp.Path;
            File.WriteAllText(Path.Combine(capturedPath, "some-file.txt"), "content");
            throw new InvalidOperationException("simulated test failure");
        };

        Assert.Throws<InvalidOperationException>(act);

        Assert.False(string.IsNullOrEmpty(capturedPath));
        Assert.False(Directory.Exists(capturedPath));
    }

    [Fact]
    public void ForceDelete_WhenPathDoesNotExist_DoesNotThrow()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "pw-tests-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missingPath));

        TempDirectory.ForceDelete(missingPath);

        Assert.False(Directory.Exists(missingPath));
    }

    [Fact]
    public void ForceDelete_WhenFileIsReadOnly_ClearsAttributeAndDeletes()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pw-tests-force-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "readonly.txt");
        File.WriteAllText(filePath, "content");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        TempDirectory.ForceDelete(root);

        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(filePath));
    }
}
