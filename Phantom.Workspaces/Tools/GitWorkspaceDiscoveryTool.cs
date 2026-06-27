using System.Text.Json.Nodes;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public sealed class GitWorkspaceDiscoveryTool : IWorkspaceTool
{
    private readonly ILogger<GitWorkspaceDiscoveryTool> logger;
    private readonly ILocalDriveRootProvider localDriveRootProvider;

    public GitWorkspaceDiscoveryTool(
        ILocalDriveRootProvider? localDriveRootProvider = null,
        ILogger<GitWorkspaceDiscoveryTool>? logger = null)
    {
        this.localDriveRootProvider = localDriveRootProvider ?? new LocalDriveRootProvider();
        this.logger = logger ?? NullLogger<GitWorkspaceDiscoveryTool>.Instance;
    }

    public string ToolType => "git-workspace-discovery";

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
            foreach (var discoveredWorktreePath in DiscoverGitWorktreePaths(scanRoot))
            {
                discoveredWorktreePaths.Add(discoveredWorktreePath);
            }
        }

        foreach (var discoveredWorktreePath in discoveredWorktreePaths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var gitMetadata = this.GetGitMetadata(discoveredWorktreePath);
            var gitWorktreeEntityName = new EntityName("git-worktrees", discoveredWorktreePath);
            var names = new JsonArray(new JsonArray("git-worktrees", discoveredWorktreePath));
            if (currentProfileNames.Length > 0)
            {
                names.Add(new JsonArray(currentProfileNames[0].Components.Select(component => (JsonNode)component).ToArray()));
            }

            var gitObject = new JsonObject();
            if (!string.IsNullOrWhiteSpace(gitMetadata.BranchName))
            {
                gitObject["branch"] = gitMetadata.BranchName;
            }

            if (!string.IsNullOrWhiteSpace(gitMetadata.HeadCommitHash))
            {
                gitObject["head-commit"] = gitMetadata.HeadCommitHash;
            }

            if (!string.IsNullOrWhiteSpace(gitMetadata.OriginRemoteUrl))
            {
                gitObject["remotes"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "origin",
                        ["url"] = gitMetadata.OriginRemoteUrl,
                    });
            }

            var entityData = new JsonObject
            {
                ["entity-types"] = new JsonArray("entity", "git-worktree", "filesystem-path"),
                ["names"] = names,
                ["display-name"] = new JsonObject
                {
                    ["default"] = Path.GetFileName(discoveredWorktreePath),
                },
                ["path"] = discoveredWorktreePath,
            };
            if (gitObject.Count > 0)
            {
                entityData["git"] = gitObject;
            }

            _ = await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
                context.DataAccessLayer,
                gitWorktreeEntityName,
                entityData,
                "Discover git worktree entities.",
                context.CancellationToken);
        }

        return new WorkspaceToolExecutionResult();
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

    private static IEnumerable<string> DiscoverGitWorktreePaths(
        string scanRoot)
    {
        var normalizedRoot = Path.GetFullPath(scanRoot);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(normalizedRoot);

        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            if (!visitedDirectories.Add(currentDirectory))
            {
                continue;
            }

            if (ContainsGitMetadata(currentDirectory))
            {
                yield return currentDirectory;
                continue;
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(currentDirectory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                pendingDirectories.Push(childDirectory);
            }
        }
    }

    private static bool ContainsGitMetadata(
        string directoryPath)
    {
        return Directory.Exists(Path.Combine(directoryPath, ".git"))
            || File.Exists(Path.Combine(directoryPath, ".git"));
    }

    private GitMetadata GetGitMetadata(
        string repositoryPath)
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
            this.logger.LogDebug(ex, "Path '{RepositoryPath}' looks like a repository but could not be opened.", repositoryPath);
            return new GitMetadata();
        }
        catch (LibGit2SharpException ex)
        {
            this.logger.LogDebug(ex, "Path '{RepositoryPath}' looks like a repository but could not be opened.", repositoryPath);
            return new GitMetadata();
        }
    }

    private sealed record GitMetadata
    {
        public string? BranchName { get; init; }

        public string? HeadCommitHash { get; init; }

        public string? OriginRemoteUrl { get; init; }
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
