using System.Text.Json;
using LibGit2Sharp;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitWorkspaceDiscoveryToolTests : IDisposable
{
    private readonly string temporaryRootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"git-workspace-discovery-{Guid.NewGuid():N}"));

    public GitWorkspaceDiscoveryToolTests()
    {
        Directory.CreateDirectory(this.temporaryRootPath);
    }

    [Fact]
    public async Task ExecuteAsync_ScansOnlyCurrentComputerUserProfileParticipantsAndUpsertsGitWorktreeEntities()
    {
        var currentProfileRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "current-profile-root"));
        var otherProfileRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root"));
        var currentProfileRepositoryPath = Path.GetFullPath(Path.Combine(currentProfileRoot, "repo-a"));
        var otherProfileRepositoryPath = Path.GetFullPath(Path.Combine(otherProfileRoot, "repo-b"));

        InitializeGitRepository(currentProfileRepositoryPath, "https://example.com/current.git");
        InitializeGitRepository(otherProfileRepositoryPath, "https://example.com/other.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            currentProfileRoot,
            otherProfileRoot);
        var tool = new GitWorkspaceDiscoveryTool(new FixedLocalDriveRootProvider([currentProfileRoot]));

        await tool.ExecuteAsync(context);

        var discoveredCurrentWorktree = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("git-worktrees", currentProfileRepositoryPath));
        Assert.NotNull(discoveredCurrentWorktree);
        var currentRawData = discoveredCurrentWorktree.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"git-worktree\"", currentRawData, StringComparison.Ordinal);
        Assert.Contains($"\"path\":\"{EscapeForJsonString(currentProfileRepositoryPath)}\"", currentRawData, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapeForJsonString(otherProfileRepositoryPath), currentRawData, StringComparison.Ordinal);

        await tool.ExecuteAsync(context);
        Assert.Equal(
            1,
            await CountEntitiesByNameAsync(dataAccessLayer, new EntityName("git-worktrees", currentProfileRepositoryPath)));
        Assert.Equal(
            0,
            await CountEntitiesByNameAsync(dataAccessLayer, new EntityName("git-worktrees", otherProfileRepositoryPath)));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunningAgainstComputerUserProfile_ScansProvidedLocalDrives()
    {
        var currentProfileRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "local-drive-root"));
        var repositoryPath = Path.GetFullPath(Path.Combine(currentProfileRoot, "repo-drive-scan"));
        InitializeGitRepository(repositoryPath, "https://example.com/drive.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            currentProfileRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));
        context = context with
        {
            Participants = [context.CurrentComputerUserProfileEntity],
        };

        var tool = new GitWorkspaceDiscoveryTool(new FixedLocalDriveRootProvider([currentProfileRoot]));
        await tool.ExecuteAsync(context);

        var discoveredWorktree = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", repositoryPath));
        Assert.NotNull(discoveredWorktree);
    }

    public void Dispose()
    {
        TryDeleteDirectory(this.temporaryRootPath);
    }

    private static void InitializeGitRepository(
        string repositoryPath,
        string remoteUrl)
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

    private static async Task<WorkspaceToolExecutionContext> CreateExecutionContextAsync(
        IDataAccessLayer dataAccessLayer,
        string currentProfileRoot,
        string otherProfileRoot)
    {
        var currentComputerEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("11111111-1111-1111-1111-111111111111"),
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["entity", "computer"],
              "names": [["computers", "hostname", "test-computer"]]
            }
            """,
            concurrencyTag: null);
        var currentUserEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("22222222-2222-2222-2222-222222222222"),
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "user"],
              "names": [["users", "username", "test-user"]]
            }
            """,
            concurrencyTag: null);
        var currentComputerUserProfileEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("33333333-3333-3333-3333-333333333333"),
            $$"""
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "test-user", "computers", "hostname", "test-computer"]],
              "computer-reference": ["computers", "hostname", "test-computer"],
              "user-reference": ["users", "username", "test-user"],
              "home-directory": "{{EscapeForJsonString(currentProfileRoot)}}"
            }
            """,
            concurrencyTag: null);
        var otherComputerUserProfileParticipant = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("44444444-4444-4444-4444-444444444444"),
            $$"""
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "other-user", "computers", "hostname", "other-computer"]],
              "computer-reference": ["computers", "hostname", "other-computer"],
              "user-reference": ["users", "username", "other-user"],
              "home-directory": "{{EscapeForJsonString(otherProfileRoot)}}"
            }
            """,
            concurrencyTag: null);
        var currentFilesystemFolderParticipant = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("55555555-5555-5555-5555-555555555555"),
            $$"""
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity", "filesystem-folder", "filesystem-path"],
              "names": [
                ["filesystem-folders", "current-profile-root"],
                ["computer-user-profiles", "users", "username", "test-user", "computers", "hostname", "test-computer"]
              ],
              "path": "{{EscapeForJsonString(currentProfileRoot)}}"
            }
            """,
            concurrencyTag: null);
        var otherFilesystemFolderParticipant = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("66666666-6666-6666-6666-666666666666"),
            $$"""
            {
              "entity-id": "66666666-6666-6666-6666-666666666666",
              "entity-types": ["entity", "filesystem-folder", "filesystem-path"],
              "names": [
                ["filesystem-folders", "other-profile-root"],
                ["computer-user-profiles", "users", "username", "other-user", "computers", "hostname", "other-computer"]
              ],
              "path": "{{EscapeForJsonString(otherProfileRoot)}}"
            }
            """,
            concurrencyTag: null);

        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = currentComputerEntity,
            CurrentUserEntity = currentUserEntity,
            CurrentComputerUserProfileEntity = currentComputerUserProfileEntity,
            ToolRelationship = currentComputerUserProfileEntity,
            Participants =
            [
                currentComputerUserProfileEntity,
                otherComputerUserProfileParticipant,
                currentFilesystemFolderParticipant,
                otherFilesystemFolderParticipant,
            ],
            Tool = currentUserEntity,
            Schedule = currentComputerEntity,
        };
    }

    private static async Task<int> CountEntitiesByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            });

        return getResult.Batches.SelectMany(static batch => batch.Entities).Count();
    }

    private static async Task<EntitySnapshot?> GetEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            });
        return getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
    }

    private static async Task<EntitySnapshot> UpsertEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Git workspace discovery test upsert.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });

        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
    }

    private static string EscapeForJsonString(
        string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(
        string directoryPath)
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

    private sealed class FixedLocalDriveRootProvider(
        IReadOnlyCollection<string> localDriveRoots) : ILocalDriveRootProvider
    {
        public IReadOnlyCollection<string> GetLocalDriveRoots()
        {
            return localDriveRoots;
        }
    }
}
