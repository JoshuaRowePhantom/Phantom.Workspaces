using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitWorkspaceUpdateToolTests : IDisposable
{
    private readonly string temporaryRootPath = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), $"git-workspace-update-{Guid.NewGuid():N}"));

    public GitWorkspaceUpdateToolTests()
    {
        Directory.CreateDirectory(this.temporaryRootPath);
    }

    public void Dispose()
    {
        TryDeleteDirectory(this.temporaryRootPath);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesGitFieldsOnExistingGitEntity()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "real-repo"));
        var remoteUrl = "https://example.com/repo.git";
        InitializeGitRepository(repoPath, remoteUrl);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var entityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            $$"""
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
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
