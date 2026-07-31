using System.Text.Json.Nodes;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public sealed class GitWorkspaceScanTool : IWorkspaceTool
{
    private readonly ILogger<GitWorkspaceScanTool> logger;
    private readonly ILocalDriveRootProvider localDriveRootProvider;

    public GitWorkspaceScanTool(
        ILocalDriveRootProvider? localDriveRootProvider = null,
        ILogger<GitWorkspaceScanTool>? logger = null)
    {
        this.localDriveRootProvider = localDriveRootProvider ?? new LocalDriveRootProvider();
        this.logger = logger ?? NullLogger<GitWorkspaceScanTool>.Instance;
    }

    public string ToolType => "git-workspace-scan";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        var currentProfileNames = WorkspaceEntitySnapshotReader.GetEntityNames(context.CurrentComputerUserProfileEntity)
            .ToArray();
        var scanRoots = this.GetScanRoots(
                context.Participants,
                context.CurrentComputerUserProfileEntity,
                currentProfileNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToArray();

        var discoveredWorktreePaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scanRoot in scanRoots)
        {
            this.logger.LogInformation("Scanning top-level directory: {Path}", scanRoot);
            var countBefore = discoveredWorktreePaths.Count;
            foreach (var discoveredWorktreePath in GitRepositoryMetadataReader.EnumerateGitRepositories(
                         scanRoot, int.MaxValue, context.CancellationToken, this.logger))
            {
                discoveredWorktreePaths.Add(discoveredWorktreePath);
            }

            this.logger.LogInformation("Found {Count} git repositories in {Path}", discoveredWorktreePaths.Count - countBefore, scanRoot);
        }

        // owning-repository is null for root/standalone repos; set to root repo path for linked worktrees.
        var allWorktrees = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in discoveredWorktreePaths)
        {
            allWorktrees[path] = null;
        }

        // Enumerate linked worktrees for every root repo found by the filesystem scan.
        // Root repos have a .git directory; linked worktrees have a .git file.
        foreach (var rootRepoPath in discoveredWorktreePaths.Where(
                     p => Directory.Exists(Path.Combine(p, ".git"))))
        {
            GitRepositoryMetadataReader.EnumerateLinkedWorktrees(rootRepoPath, this.logger,
                linkedWorktreePath =>
                {
                    allWorktrees[linkedWorktreePath] = rootRepoPath;
                });
        }

        foreach (var (discoveredWorktreePath, owningRepositoryPath) in allWorktrees)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var gitMetadata = GitRepositoryMetadataReader.TryReadMetadata(discoveredWorktreePath, this.logger);
            var normalizedPath = NormalizeRepositoryPath(discoveredWorktreePath);
            var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

            var entityData = GitWorkspaceEntityData.Build(
                discoveredWorktreePath,
                currentProfileNames,
                gitMetadata,
                owningRepositoryPath);

            _ = await WorkspaceToolEntityUtilities.UpsertEntityByDeterministicIdAsync(
                context.DataAccessLayer,
                deterministicId,
                entityData,
                GitWorkspaceEntityData.MergePreservingUserEditableFields,
                "Discover git worktree entities.",
                context.CancellationToken);
        }

        var summary = $"Scanned {scanRoots.Length} root(s); found {discoveredWorktreePaths.Count} repositories, " +
            $"indexed {allWorktrees.Count} worktree(s).";
        this.logger.LogInformation("{Summary}", summary);
        return WorkspaceToolExecutionResult.Success() with { ResultContent = summary };
    }

    private IEnumerable<string> GetScanRoots(
        IReadOnlyCollection<EntitySnapshot> participants,
        EntitySnapshot currentComputerUserProfileEntity,
        IReadOnlyCollection<EntityName> currentProfileNames)
    {
        var currentContextTypes = WorkspaceEntitySnapshotReader.GetEntityTypes(currentComputerUserProfileEntity);
        if (currentContextTypes.Contains("user-computer-profile", StringComparer.Ordinal))
        {
            foreach (var localDriveRoot in this.localDriveRootProvider.GetLocalDriveRoots())
            {
                if (!string.IsNullOrWhiteSpace(localDriveRoot))
                {
                    yield return localDriveRoot;
                }
            }
        }

        foreach (var participant in participants)
        {
            var participantTypes = WorkspaceEntitySnapshotReader.GetEntityTypes(participant);
            if (participantTypes.Contains("user-computer-profile", StringComparer.Ordinal)
                && participant.EntityId == currentComputerUserProfileEntity.EntityId)
            {
                var homeDirectoryPath = WorkspaceEntitySnapshotReader.TryGetStringProperty(participant, "home-directory");
                if (!string.IsNullOrWhiteSpace(homeDirectoryPath))
                {
                    yield return homeDirectoryPath;
                }
            }

            if (participantTypes.Contains("filesystem-folder", StringComparer.Ordinal)
                || participantTypes.Contains("filesystem-path", StringComparer.Ordinal)
                || participantTypes.Contains("folder", StringComparer.Ordinal))
            {
                if (!IsForCurrentProfile(participant, currentProfileNames))
                {
                    continue;
                }

                var path = WorkspaceEntitySnapshotReader.TryGetStringProperty(participant, "path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static bool IsForCurrentProfile(
        EntitySnapshot participant,
        IReadOnlyCollection<EntityName> currentProfileNames)
    {
        var participantNames = WorkspaceEntitySnapshotReader.GetEntityNames(participant);
        return participantNames.Any(participantName => currentProfileNames.Contains(participantName));
    }

    private static string NormalizeRepositoryPath(string repositoryPath)
    {
        return Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }
}

public interface ILocalDriveRootProvider
{
    IReadOnlyCollection<string> GetLocalDriveRoots();
}

public sealed class LocalDriveRootProvider : ILocalDriveRootProvider
{
    public IReadOnlyCollection<string> GetLocalDriveRoots()
    {
        return DriveInfo.GetDrives()
            .Where(static drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(static drive => drive.RootDirectory.FullName)
            .ToArray();
    }
}
