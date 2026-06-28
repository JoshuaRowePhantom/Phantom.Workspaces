using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools.GitHub;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitHubWorkItemDiscoveryToolTests
{
    private static WorkspaceToolExecutionContext CreateContext(
        IDataAccessLayer dataAccessLayer,
        params EntitySnapshot[] participants)
    {
        var dummyEntity = CreateDummyEntity(new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "dummy");
        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = dummyEntity,
            CurrentUserEntity = dummyEntity,
            CurrentComputerUserProfileEntity = dummyEntity,
            ToolRelationship = dummyEntity,
            Participants = participants,
            Tool = dummyEntity,
            Schedule = dummyEntity,
        };
    }

    private static EntitySnapshot CreateDummyEntity(EntityId entityId, string name)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": ["entity"],
              "names": [["test", "{{name}}"]]
            }
            """);
        return new EntitySnapshot
        {
            EntityId = entityId,
            ModifiedTime = new Timestamp(),
            Relationships = [],
            Data = document.RootElement.Clone(),
        };
    }

    private static EntitySnapshot CreateGitRepositoryEntity(string entityId, string githubUrl)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git-repository", "external"],
              "names": [["git-repositories", "test-repo"]],
              "display-name": {"default": "Test Repo"},
              "urls": {"default": "{{githubUrl}}"}
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId(Guid.Parse(entityId)),
            ModifiedTime = new Timestamp(),
            Relationships = [],
            Data = document.RootElement.Clone(),
        };
    }

    private static async Task<EntitySnapshot?> GetEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityName = entityName }],
        });
        return result.Batches.SelectMany(static b => b.Entities).FirstOrDefault();
    }

    private static async Task<int> CountEntitiesWithNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityName = entityName }],
        });
        return result.Batches.SelectMany(static b => b.Entities).Count();
    }

    private static async Task<EntitySnapshot> UpsertEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Test upsert." } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                    Data = document.RootElement.Clone(),
                },
            ],
        });
        var entityResult = Assert.Single(updateResult.EntityResults, e => e.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
    }

    [Fact]
    public async Task GitHubWorkItemDiscoveryTool_MapsIssueFieldsToWorkItemEntity()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateGitRepositoryEntity(
            "11111111-1111-1111-1111-111111111111",
            "https://github.com/myorg/myrepo");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var issueJson = """
            [
              {
                "number": 7,
                "title": "Fix the bug",
                "state": "OPEN",
                "body": "A bug was found.",
                "labels": [{"name": "bug"}, {"name": "priority"}],
                "url": "https://github.com/myorg/myrepo/issues/7",
                "createdAt": "2024-01-01T00:00:00Z",
                "updatedAt": "2024-01-02T00:00:00Z"
              }
            ]
            """;

        var tool = new GitHubWorkItemDiscoveryTool(
            issueListRunner: (_, _) => Task.FromResult(issueJson));

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("github", "myorg", "myrepo", "work-items", "7"));
        Assert.NotNull(entity);

        var raw = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"git-work-item\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"work-item\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"external\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"open\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"bug\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"priority\"", raw, StringComparison.Ordinal);
        Assert.Contains("https://github.com/myorg/myrepo/issues/7", raw, StringComparison.Ordinal);
        Assert.Contains("Fix the bug", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubWorkItemDiscoveryTool_SkipsIssuesThatArePullRequests()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateGitRepositoryEntity(
            "22222222-2222-2222-2222-222222222222",
            "https://github.com/myorg/myrepo");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var issueJson = """
            [
              {
                "number": 3,
                "title": "A pull request",
                "state": "OPEN",
                "body": "This is actually a PR.",
                "labels": [],
                "url": "https://github.com/myorg/myrepo/pull/3",
                "pull_request": {"url": "https://api.github.com/repos/myorg/myrepo/pulls/3"},
                "createdAt": "2024-01-01T00:00:00Z",
                "updatedAt": "2024-01-02T00:00:00Z"
              }
            ]
            """;

        var tool = new GitHubWorkItemDiscoveryTool(
            issueListRunner: (_, _) => Task.FromResult(issueJson));

        await tool.ExecuteAsync(context);

        var count = await CountEntitiesWithNameAsync(
            dataAccessLayer,
            new EntityName("github", "myorg", "myrepo", "work-items", "3"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GitHubWorkItemDiscoveryTool_WritesRelatedRelationship_WhenLinkedPrEntityExists()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateGitRepositoryEntity(
            "33333333-3333-3333-3333-333333333333",
            "https://github.com/myorg/myrepo");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var prEntityId = new EntityId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        await UpsertEntityAsync(dataAccessLayer, prEntityId, $$"""
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity", "git-pull-request", "external"],
              "names": [["github", "myorg", "myrepo", "pull-requests", "42"]],
              "display-name": {"default": "PR #42"},
              "urls": {"default": "https://github.com/myorg/myrepo/pull/42"}
            }
            """);

        var issueJson = """
            [
              {
                "number": 1,
                "title": "Issue linked to PR",
                "state": "OPEN",
                "body": "A bug.",
                "labels": [],
                "url": "https://github.com/myorg/myrepo/issues/1",
                "createdAt": "2024-01-01T00:00:00Z",
                "updatedAt": "2024-01-02T00:00:00Z"
              }
            ]
            """;

        var timelineJson = """
            [
              {
                "event": "cross-referenced",
                "source": {
                  "type": "issue",
                  "issue": {
                    "number": 42,
                    "pull_request": {"url": "https://api.github.com/repos/myorg/myrepo/pulls/42"}
                  }
                }
              }
            ]
            """;

        var tool = new GitHubWorkItemDiscoveryTool(
            issueListRunner: (_, _) => Task.FromResult(issueJson),
            timelineRunner: (_, _, _) => Task.FromResult(timelineJson));

        await tool.ExecuteAsync(context);

        var workItemEntity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("github", "myorg", "myrepo", "work-items", "1"));
        Assert.NotNull(workItemEntity);

        var relationshipName = new EntityName(
            "github", "myorg", "myrepo", "work-items", "1", "related", "44444444-4444-4444-4444-444444444444");
        var relEntity = await GetEntityByNameAsync(dataAccessLayer, relationshipName);
        Assert.NotNull(relEntity);
        var relRaw = relEntity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"related\"", relRaw, StringComparison.Ordinal);
        Assert.Contains(workItemEntity.EntityId.Value.ToString(), relRaw, StringComparison.Ordinal);
        Assert.Contains("44444444-4444-4444-4444-444444444444", relRaw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubWorkItemDiscoveryTool_ClosedIssue_MapsToClosedStatus()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateGitRepositoryEntity(
            "55555555-5555-5555-5555-555555555555",
            "https://github.com/myorg/myrepo");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var issueJson = """
            [
              {
                "number": 10,
                "title": "Fixed issue",
                "state": "CLOSED",
                "body": "",
                "labels": [],
                "url": "https://github.com/myorg/myrepo/issues/10",
                "createdAt": "2024-01-01T00:00:00Z",
                "updatedAt": "2024-01-03T00:00:00Z"
              }
            ]
            """;

        var tool = new GitHubWorkItemDiscoveryTool(
            issueListRunner: (_, _) => Task.FromResult(issueJson));

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("github", "myorg", "myrepo", "work-items", "10"));
        Assert.NotNull(entity);
        Assert.Contains("\"closed\"", entity.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubWorkItemDiscoveryTool_UpsertIsIdempotent_DoesNotDuplicateEntity()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateGitRepositoryEntity(
            "66666666-6666-6666-6666-666666666666",
            "https://github.com/myorg/myrepo");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var issueJson = """
            [
              {
                "number": 5,
                "title": "Duplicate test",
                "state": "OPEN",
                "body": "",
                "labels": [],
                "url": "https://github.com/myorg/myrepo/issues/5",
                "createdAt": "2024-01-01T00:00:00Z",
                "updatedAt": "2024-01-01T00:00:00Z"
              }
            ]
            """;

        var tool = new GitHubWorkItemDiscoveryTool(
            issueListRunner: (_, _) => Task.FromResult(issueJson));

        await tool.ExecuteAsync(context);
        await tool.ExecuteAsync(context);

        var count = await CountEntitiesWithNameAsync(
            dataAccessLayer,
            new EntityName("github", "myorg", "myrepo", "work-items", "5"));
        Assert.Equal(1, count);
    }
}
