using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitWorkspaceUpdateToolTests : IDisposable
{
    private readonly TempDirectory temporaryRoot = new("git-workspace-update-");
    private string temporaryRootPath => this.temporaryRoot.Path;

    public void Dispose()
    {
        this.temporaryRoot.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesGitFieldsOnExistingGitEntity()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "real-repo"));
        var remoteUrl = "https://example.com/repo.git";
        InitializeGitRepository(repoPath, remoteUrl);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var entityId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": { "default": "real-repo" },
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        var updatedEntity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.NotNull(updatedEntity?.Data);
        var rawData = updatedEntity.Data!.Value.GetRawText();
        var entityObject = JsonNode.Parse(rawData)!.AsObject();
        var git = entityObject["git"]?.AsObject();
        Assert.NotNull(git);
        Assert.False(string.IsNullOrWhiteSpace(git["branch"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(git["head-commit"]?.GetValue<string>()));

        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntitiesWithNoPath()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var entityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            """
            {
              "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity", "git"],
              "names": [["git", "no-path"]],
              "display-name": { "default": "no-path" }
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        var entityAfterNoPath = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.NotNull(entityAfterNoPath);
        var gitSubObjectNoPath = JsonNode.Parse(entityAfterNoPath!.Data!.Value.GetRawText())?["git"];
        Assert.Null(gitSubObjectNoPath);
        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntitiesWithInvalidPath()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var entityId = new EntityId("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var missingPath = Path.Combine(this.temporaryRootPath, "does-not-exist");
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            $$"""
            {
              "entity-id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(missingPath)}}"]],
              "display-name": { "default": "missing" },
              "path": "{{EscapeForJsonString(missingPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        var entityAfterInvalidPath = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.NotNull(entityAfterInvalidPath);
        var gitSubObjectInvalidPath = JsonNode.Parse(entityAfterInvalidPath!.Data!.Value.GetRawText())?["git"];
        Assert.Null(gitSubObjectInvalidPath);
        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResultContentWithSummary()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "summary-repo"));
        InitializeGitRepository(repoPath, "https://example.com/summary.git");
        var missingPath = Path.Combine(this.temporaryRootPath, "summary-missing");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            $$"""
            {
              "entity-id": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(repoPath)}}"]],
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);
        await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            $$"""
            {
              "entity-id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(missingPath)}}"]],
              "path": "{{EscapeForJsonString(missingPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        Assert.NotNull(result.ResultContent);
        Assert.Contains("1", result.ResultContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_PreservesDisplayName()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-display"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-display.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "CustomRefreshName"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        var displayName = refreshedEntity.Data?.GetProperty("display-name").GetProperty("default").GetString();
        Assert.Equal("CustomRefreshName", displayName);
    }

    [Fact]
    public async Task Refresh_PreservesNames()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-names"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-names.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["custom-refresh-name", "preserved"], ["another", "name"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        var names = refreshedEntity.Data?.GetProperty("names").EnumerateArray().ToList();
        Assert.Equal(2, names?.Count);
        Assert.Equal("custom-refresh-name", names?[0].EnumerateArray().First().GetString());
        Assert.Equal("preserved", names?[0].EnumerateArray().Skip(1).First().GetString());
    }

    [Fact]
    public async Task Refresh_UpdatesAllGitFields()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-git-fields"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-git-fields.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}",
              "git": {"branch": "old-branch", "head-commit": "old-commit"}
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        var git = refreshedEntity.Data?.GetProperty("git");
        Assert.True(git.HasValue);
        var hasBranch = git.Value.TryGetProperty("branch", out var branch);
        var hasHeadCommit = git.Value.TryGetProperty("head-commit", out var headCommit);
        Assert.True(hasBranch);
        Assert.True(hasHeadCommit);
        Assert.False(string.IsNullOrWhiteSpace(branch.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(headCommit.GetString()));
    }

    [Fact]
    public async Task Refresh_UsesDeterministicId_PreservesEntityId()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-id"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-id.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        Assert.Equal(deterministicId, refreshedEntity.EntityId);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatedWorktree_SetsComputerUserProfileIdToCurrentProfileEntityId()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "update-profile-id"));
        InitializeGitRepository(repoPath, "https://example.com/update-profile-id.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        // Pre-existing entity WITHOUT computer-user-profile-id — simulating a worktree
        // written by a pre-fix scanner version.
        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "update-profile-id"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = CreateContext(dataAccessLayer);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        Assert.True(refreshedEntity.Data!.Value.TryGetProperty("computer-user-profile-id", out var profileIdElement));
        Assert.Equal(context.CurrentComputerUserProfileEntity.EntityId.ToString(), profileIdElement.GetString());
    }

    private static WorkspaceToolExecutionContext CreateContext(IDataAccessLayer dataAccessLayer)
    {
        var placeholder = CreateSnapshot(
            """
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["entity"],
              "names": [["placeholder"]]
            }
            """);
        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = placeholder,
            CurrentUserEntity = placeholder,
            CurrentComputerUserProfileEntity = placeholder,
            ToolRelationship = placeholder,
            Participants = [placeholder],
            Tool = CreateSnapshot("""{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-update" }"""),
            Schedule = placeholder,
        };
    }

    private static EntitySnapshot CreateSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entityId = TryReadEntityId(document.RootElement) ?? new EntityId(Guid.NewGuid());
        return new EntitySnapshot
        {
            EntityId = entityId,
            ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
            Data = document.RootElement.Clone(),
            Relationships = [],
        };
    }

    private static EntityId? TryReadEntityId(JsonElement element)
    {
        if (element.TryGetProperty("entity-id", out var entityIdElement)
            && entityIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(entityIdElement.GetString(), out var guid))
        {
            return new EntityId(guid);
        }

        return null;
    }

    private static async Task<EntitySnapshot?> GetEntityByIdAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                    },
                ],
            });
        return getResult.Batches.SelectMany(static b => b.Entities).FirstOrDefault();
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
                    Comment = new Markdown { Text = "GitWorkspaceUpdateTool test upsert." },
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

        var entityResult = Assert.Single(updateResult.EntityResults, r => r.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
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

    private static string EscapeForJsonString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);
}
