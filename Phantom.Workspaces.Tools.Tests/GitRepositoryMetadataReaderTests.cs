using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitRepositoryMetadataReaderTests : IDisposable
{
    private readonly string temporaryRootPath = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), $"git-metadata-reader-{Guid.NewGuid():N}"));

    public GitRepositoryMetadataReaderTests()
    {
        Directory.CreateDirectory(this.temporaryRootPath);
    }

    public void Dispose()
    {
        TryDeleteDirectory(this.temporaryRootPath);
    }

    [Fact]
    public void IsGitRepository_ReturnsTrueForDotGitDirectory()
    {
        var repoPath = Path.Combine(this.temporaryRootPath, "with-git-dir");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));

        Assert.True(GitRepositoryMetadataReader.IsGitRepository(repoPath));
    }

    [Fact]
    public void IsGitRepository_ReturnsTrueForDotGitFile()
    {
        var repoPath = Path.Combine(this.temporaryRootPath, "with-git-file");
        Directory.CreateDirectory(repoPath);
        File.WriteAllText(Path.Combine(repoPath, ".git"), "gitdir: ../.git/worktrees/branch");

        Assert.True(GitRepositoryMetadataReader.IsGitRepository(repoPath));
    }

    [Fact]
    public void IsGitRepository_ReturnsFalseForPlainDirectory()
    {
        var plainPath = Path.Combine(this.temporaryRootPath, "plain-directory");
        Directory.CreateDirectory(plainPath);

        Assert.False(GitRepositoryMetadataReader.IsGitRepository(plainPath));
    }

    [Fact]
    public void TryReadMetadata_ReturnsMetadata_ForValidRepository()
    {
        var repoPath = Path.Combine(this.temporaryRootPath, "valid-repo");
        InitializeGitRepository(repoPath, "https://example.com/valid.git");

        var metadata = GitRepositoryMetadataReader.TryReadMetadata(repoPath, NullLogger.Instance);

        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrWhiteSpace(metadata.BranchName));
        Assert.False(string.IsNullOrWhiteSpace(metadata.HeadCommitHash));
        Assert.Equal("https://example.com/valid.git", metadata.OriginRemoteUrl);
    }

    [Fact]
    public void TryReadMetadata_ReturnsNull_WhenRepositoryCannotBeOpened()
    {
        var invalidRepoPath = Path.Combine(this.temporaryRootPath, "invalid-repo");
        Directory.CreateDirectory(Path.Combine(invalidRepoPath, ".git"));

        var metadata = GitRepositoryMetadataReader.TryReadMetadata(invalidRepoPath, NullLogger.Instance);

        Assert.Null(metadata);
    }

    private static void InitializeGitRepository(string repositoryPath, string remoteUrl)
    {
        Directory.CreateDirectory(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), "# test");
        Repository.Init(repositoryPath);

        using var repository = new Repository(repositoryPath);
        repository.Config.Set("user.name", "test-user");
        repository.Config.Set("user.email", "test@example.com");
        Commands.Stage(repository, "*");
        var signature = new Signature("test-user", "test@example.com", DateTimeOffset.UtcNow);
        repository.Commit("initial", signature, signature);
        repository.Network.Remotes.Add("origin", remoteUrl);
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
