using System;
using System.Collections.Generic;
using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Shared helper for detecting and reading metadata from Git repositories.
/// Used by <see cref="GitWorkspaceScanTool"/> and <see cref="GitWorkspaceUpdateTool"/>
/// to avoid duplicating detection and read logic.
/// </summary>
public static class GitRepositoryMetadataReader
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="path"/> is the root of a Git repository
    /// (contains a <c>.git</c> directory or a <c>.git</c> file as used by worktrees).
    /// </summary>
    public static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    /// <summary>
    /// Walks the directory tree rooted at <paramref name="root"/>, yielding the path of each Git
    /// repository found. A repository is treated as a leaf — its subdirectories are not descended
    /// into. The walk stops descending beyond <paramref name="maxDepth"/> levels.
    /// </summary>
    public static IEnumerable<string> EnumerateGitRepositories(
        string root,
        int maxDepth,
        CancellationToken cancellationToken,
        ILogger? logger = null)
        => EnumerateGitRepositories(root, maxDepth, Array.Empty<string>(), cancellationToken, logger);

    /// <summary>
    /// Overload accepting a collection of excluded paths. Any directory whose full path equals one
    /// of the (already-expanded, already-normalized) excludes, or lies under it (with a directory
    /// separator boundary), is skipped along with its entire subtree. Comparison is case-insensitive.
    /// </summary>
    public static IEnumerable<string> EnumerateGitRepositories(
        string root,
        int maxDepth,
        IReadOnlyCollection<string> excludes,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var pending = new Stack<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootFullPath = Path.GetFullPath(root);

        if (IsUnderAnyExclude(rootFullPath, excludes))
        {
            logger?.LogDebug("Skipping excluded directory: {Path}", rootFullPath);
            yield break;
        }

        pending.Push((rootFullPath, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = pending.Pop();

            if (!visited.Add(path))
            {
                continue;
            }

            if (IsGitRepository(path))
            {
                logger?.LogDebug("Found git repository: {RepoPath}", path);
                yield return path;
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                logger?.LogDebug(exception, "Skipping inaccessible directory during git scan: {Path}", path);
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (string.Equals(name, ".git", StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsUnderAnyExclude(subdirectory, excludes))
                {
                    logger?.LogDebug("Skipping excluded directory: {Path}", subdirectory);
                    continue;
                }

                pending.Push((subdirectory, depth + 1));
            }
        }
    }

    /// <summary>
    /// Returns true if <paramref name="path"/> is equal to, or lies under, any of the supplied
    /// exclude paths. Excludes are expected to be already expanded (env-var substituted) and
    /// normalized via <see cref="Path.GetFullPath(string)"/> with trailing separators trimmed.
    /// A directory-separator boundary is required to avoid false positives such as
    /// <c>C:\root\TempStuff</c> matching an exclude of <c>C:\root\Temp</c>.
    /// </summary>
    internal static bool IsUnderAnyExclude(string path, IReadOnlyCollection<string> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var exclude in excludes)
        {
            if (string.IsNullOrEmpty(exclude))
            {
                continue;
            }

            if (string.Equals(fullPath, exclude, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fullPath.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fullPath.StartsWith(exclude + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens the root repository at <paramref name="rootRepoPath"/> and invokes <paramref name="onLinkedWorktree"/>
    /// for each linked worktree found. Exceptions from LibGit2Sharp are caught and logged at Debug level.
    /// </summary>
    public static void EnumerateLinkedWorktrees(
        string rootRepoPath,
        ILogger? logger,
        Action<string> onLinkedWorktree)
    {
        try
        {
            using var repository = new Repository(rootRepoPath);
            foreach (var worktree in repository.Worktrees)
            {
                try
                {
                    using var worktreeRepo = worktree.WorktreeRepository;
                    var worktreePath = Path.GetFullPath(
                        worktreeRepo.Info.WorkingDirectory.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar));
                    onLinkedWorktree(worktreePath);
                }
                catch (LibGit2SharpException ex)
                {
                    logger?.LogDebug(ex, "Could not open linked worktree '{WorktreeName}' of '{RootRepoPath}'.", worktree.Name, rootRepoPath);
                }
            }
        }
        catch (RepositoryNotFoundException ex)
        {
            logger?.LogDebug(ex, "Path '{RootRepoPath}' could not be opened to enumerate linked worktrees.", rootRepoPath);
        }
        catch (LibGit2SharpException ex)
        {
            logger?.LogDebug(ex, "Path '{RootRepoPath}' could not be opened to enumerate linked worktrees.", rootRepoPath);
        }
    }

    /// <summary>
    /// Opens the repository at <paramref name="repositoryPath"/> and returns its current metadata,
    /// or <see langword="null"/> if the repository cannot be opened (logged at Debug level).
    /// </summary>
    public static GitMetadata? TryReadMetadata(string repositoryPath, ILogger logger)
    {
        try
        {
            using var repository = new Repository(repositoryPath);
            return new GitMetadata
            {
                BranchName = repository.Head.FriendlyName,
                HeadCommitHash = repository.Head.Tip?.Sha,
                OriginRemoteUrl = repository.Network.Remotes["origin"]?.Url,
            };
        }
        catch (RepositoryNotFoundException ex)
        {
            logger.LogDebug(ex, "Path '{RepositoryPath}' looks like a repository but could not be opened.", repositoryPath);
            return null;
        }
        catch (LibGit2SharpException ex)
        {
            logger.LogDebug(ex, "Path '{RepositoryPath}' looks like a repository but could not be opened.", repositoryPath);
            return null;
        }
    }
}

/// <summary>Git repository metadata returned by <see cref="GitRepositoryMetadataReader"/>.</summary>
public sealed record GitMetadata
{
    public string? BranchName { get; init; }

    public string? HeadCommitHash { get; init; }

    public string? OriginRemoteUrl { get; init; }
}
