using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitRepositoryMetadataReaderTests : IDisposable
{
    private readonly TempDirectory temporaryRoot = new("git-metadata-reader-");
    private string temporaryRootPath => this.temporaryRoot.Path;

    public void Dispose()
    {
        this.temporaryRoot.Dispose();
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

    [Fact]
    public void EnumerateLinkedWorktrees_InvokesCallbackForEachLinkedWorktree()
    {
        var rootRepoPath = Path.Combine(this.temporaryRootPath, "root-for-linked");
        var linkedPath = Path.Combine(this.temporaryRootPath, "linked-wt-callback");
        InitializeGitRepository(rootRepoPath, "https://example.com/linked.git");

        using (var repo = new Repository(rootRepoPath))
        {
            repo.Worktrees.Add("linked-wt", linkedPath, isLocked: false);
        }

        var collected = new List<string>();
        GitRepositoryMetadataReader.EnumerateLinkedWorktrees(rootRepoPath, null, collected.Add);

        Assert.Single(collected);
        Assert.Equal(Path.GetFullPath(linkedPath), collected[0], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnumerateLinkedWorktrees_ReportsNormalizedPath_WithNoTrailingSeparator()
    {
        var rootRepoPath = Path.Combine(this.temporaryRootPath, "root-norm");
        var linkedPath = Path.Combine(this.temporaryRootPath, "linked-norm");
        InitializeGitRepository(rootRepoPath, "https://example.com/norm.git");

        using (var repo = new Repository(rootRepoPath))
        {
            repo.Worktrees.Add("norm-wt", linkedPath, isLocked: false);
        }

        var collected = new List<string>();
        GitRepositoryMetadataReader.EnumerateLinkedWorktrees(rootRepoPath, null, collected.Add);

        Assert.Single(collected);
        var path = collected[0];
        Assert.False(
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal),
            $"Path should not have trailing separator: {path}");
    }

    [Fact]
    public void EnumerateLinkedWorktrees_WhenRepositoryCannotBeOpened_DoesNotThrow()
    {
        var invalidPath = Path.Combine(this.temporaryRootPath, "not-a-repo");
        Directory.CreateDirectory(Path.Combine(invalidPath, ".git"));

        var collected = new List<string>();
        var exception = Record.Exception(() =>
            GitRepositoryMetadataReader.EnumerateLinkedWorktrees(invalidPath, null, collected.Add));

        Assert.Null(exception);
        Assert.Empty(collected);
    }

    [Fact]
    public void EnumerateLinkedWorktrees_WhenRepoHasNoLinkedWorktrees_InvokesCallbackZeroTimes()
    {
        var rootRepoPath = Path.Combine(this.temporaryRootPath, "root-no-linked");
        InitializeGitRepository(rootRepoPath, "https://example.com/no-linked.git");

        var collected = new List<string>();
        GitRepositoryMetadataReader.EnumerateLinkedWorktrees(rootRepoPath, null, collected.Add);

        Assert.Empty(collected);
    }

    [Fact]
    public void EnumerateGitRepositories_ExcludedPrefix_SkipsSubtreeButNotSiblings()
    {
        var excludedRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "Temp"));
        var siblingRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "TempStuff"));
        var excludedRepo = Path.Combine(excludedRoot, "excluded-repo");
        var siblingRepo = Path.Combine(siblingRoot, "sibling-repo");
        InitializeGitRepository(excludedRepo, "https://example.com/excluded.git");
        InitializeGitRepository(siblingRepo, "https://example.com/sibling.git");

        var results = GitRepositoryMetadataReader
            .EnumerateGitRepositories(
                this.temporaryRootPath,
                int.MaxValue,
                new[] { excludedRoot },
                CancellationToken.None)
            .ToArray();

        Assert.DoesNotContain(results, r => string.Equals(r, excludedRepo, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, r => string.Equals(r, siblingRepo, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnumerateGitRepositories_ExcludedPathDiffersInCase_StillSkipped()
    {
        var excludeRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "Temp"));
        var repoInside = Path.Combine(excludeRoot, "repo");
        InitializeGitRepository(repoInside, "https://example.com/case.git");

        var mixedCaseExclude = excludeRoot.ToUpperInvariant();

        var results = GitRepositoryMetadataReader
            .EnumerateGitRepositories(
                this.temporaryRootPath,
                int.MaxValue,
                new[] { mixedCaseExclude },
                CancellationToken.None)
            .ToArray();

        Assert.DoesNotContain(results, r => string.Equals(r, repoInside, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnumerateGitRepositories_NoExcludes_EnumeratesAsBefore()
    {
        var repoOne = Path.Combine(this.temporaryRootPath, "no-excludes-one");
        var repoTwo = Path.Combine(this.temporaryRootPath, "no-excludes-two");
        InitializeGitRepository(repoOne, "https://example.com/no-ex-one.git");
        InitializeGitRepository(repoTwo, "https://example.com/no-ex-two.git");

        var withoutOverload = GitRepositoryMetadataReader
            .EnumerateGitRepositories(this.temporaryRootPath, int.MaxValue, CancellationToken.None)
            .Select(p => Path.GetFullPath(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var withEmptyExcludes = GitRepositoryMetadataReader
            .EnumerateGitRepositories(this.temporaryRootPath, int.MaxValue, Array.Empty<string>(), CancellationToken.None)
            .Select(p => Path.GetFullPath(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(withoutOverload, withEmptyExcludes);
        Assert.Contains(withoutOverload, r => string.Equals(r, Path.GetFullPath(repoOne), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(withoutOverload, r => string.Equals(r, Path.GetFullPath(repoTwo), StringComparison.OrdinalIgnoreCase));
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
}
