using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools.AzureDevOps;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class AzureDevOpsPullRequestDiscoveryToolTests
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

    private static EntitySnapshot CreateAzureDevOpsRepositoryEntity(
        string entityId,
        string repositoryUrl,
        string repositoryId = "repo-guid-0000-0000-0000-000000000000")
    {
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "repository", "git-repository", "azure-devops-repository", "external"],
              "names": [["azure-devops", "myorg", "myproject", "MyRepo"]],
              "display-name": {"default": "MyRepo"},
              "urls": {"default": "{{repositoryUrl}}"},
              "repository-id": "{{repositoryId}}"
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

    [Fact]
    public async Task AzureDevOpsPullRequestDiscoveryTool_UpsertsPullRequest()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntityId = "11111111-1111-1111-1111-111111111111";
        var repoEntity = CreateAzureDevOpsRepositoryEntity(
            repoEntityId,
            "https://dev.azure.com/myorg/myproject/_git/MyRepo",
            "repo-0000-0000-0000-000000000001");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var prResponse = """
            {
              "value": [
                {
                  "pullRequestId": 42,
                  "title": "Fix the bug",
                  "status": "active",
                  "isDraft": false,
                  "createdBy": { "uniqueName": "dev@example.com" },
                  "mergeStatus": "notSet",
                  "sourceRefName": "refs/heads/feature-branch",
                  "targetRefName": "refs/heads/main"
                }
              ]
            }
            """;

        var tool = new AzureDevOpsPullRequestDiscoveryTool(
            httpGetter: (url, pat, ct) => Task.FromResult(prResponse));

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "MyRepo", "pull-requests", "42"));
        Assert.NotNull(entity);

        var raw = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"azure-devops-pull-request\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"git-pull-request\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"Fix the bug\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"open\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"dev@example.com\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"refs/heads/feature-branch\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"refs/heads/main\"", raw, StringComparison.Ordinal);
        Assert.Contains(repoEntityId, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsPullRequestDiscoveryTool_MapsAdoStatusToPullRequestStatus()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateAzureDevOpsRepositoryEntity(
            "22222222-2222-2222-2222-222222222222",
            "https://dev.azure.com/myorg/myproject/_git/MyRepo",
            "repo-guid-0002");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var prResponse = """
            {
              "value": [
                {
                  "pullRequestId": 1,
                  "title": "Active PR",
                  "status": "active",
                  "isDraft": false,
                  "createdBy": { "uniqueName": "dev@example.com" },
                  "mergeStatus": "notSet",
                  "sourceRefName": "refs/heads/a",
                  "targetRefName": "refs/heads/main"
                },
                {
                  "pullRequestId": 2,
                  "title": "Completed PR",
                  "status": "completed",
                  "isDraft": false,
                  "createdBy": { "uniqueName": "dev@example.com" },
                  "mergeStatus": "succeeded",
                  "sourceRefName": "refs/heads/b",
                  "targetRefName": "refs/heads/main"
                },
                {
                  "pullRequestId": 3,
                  "title": "Abandoned PR",
                  "status": "abandoned",
                  "isDraft": false,
                  "createdBy": { "uniqueName": "dev@example.com" },
                  "mergeStatus": "notSet",
                  "sourceRefName": "refs/heads/c",
                  "targetRefName": "refs/heads/main"
                }
              ]
            }
            """;

        var tool = new AzureDevOpsPullRequestDiscoveryTool(
            httpGetter: (url, pat, ct) => Task.FromResult(prResponse));

        await tool.ExecuteAsync(context);

        var pr1 = await GetEntityByNameAsync(dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "MyRepo", "pull-requests", "1"));
        var pr2 = await GetEntityByNameAsync(dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "MyRepo", "pull-requests", "2"));
        var pr3 = await GetEntityByNameAsync(dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "MyRepo", "pull-requests", "3"));

        Assert.NotNull(pr1);
        Assert.NotNull(pr2);
        Assert.NotNull(pr3);
        Assert.Contains("\"open\"", pr1.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"merged\"", pr2.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"closed\"", pr3.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsPullRequestDiscoveryTool_IsDraftMappedToDraftStatus()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var repoEntity = CreateAzureDevOpsRepositoryEntity(
            "33333333-3333-3333-3333-333333333333",
            "https://dev.azure.com/myorg/myproject/_git/MyRepo",
            "repo-guid-0003");
        var context = CreateContext(dataAccessLayer, repoEntity);

        var prResponse = """
            {
              "value": [
                {
                  "pullRequestId": 99,
                  "title": "Draft PR",
                  "status": "active",
                  "isDraft": true,
                  "createdBy": { "uniqueName": "author@example.com" },
                  "mergeStatus": "notSet",
                  "sourceRefName": "refs/heads/draft-branch",
                  "targetRefName": "refs/heads/main"
                }
              ]
            }
            """;

        var tool = new AzureDevOpsPullRequestDiscoveryTool(
            httpGetter: (url, pat, ct) => Task.FromResult(prResponse));

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "MyRepo", "pull-requests", "99"));
        Assert.NotNull(entity);
        Assert.Contains("\"draft\"", entity.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsPullRequestDiscoveryTool_SkipsNonRepositoryParticipants()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var nonRepoEntity = CreateDummyEntity(
            new EntityId("44444444-4444-4444-4444-444444444444"), "not-a-repo");
        var context = CreateContext(dataAccessLayer, nonRepoEntity);

        var callCount = 0;
        var tool = new AzureDevOpsPullRequestDiscoveryTool(
            httpGetter: (url, pat, ct) =>
            {
                callCount++;
                return Task.FromResult("{\"value\":[]}");
            });

        var result = await tool.ExecuteAsync(context);

        Assert.Equal(0, callCount);
        Assert.Contains("0 repository/repositories", result.ResultContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("active", false, "open")]
    [InlineData("completed", false, "merged")]
    [InlineData("abandoned", false, "closed")]
    [InlineData("active", true, "draft")]
    [InlineData("completed", true, "draft")]
    public void MapAdoStatusToStatus_ReturnsExpectedMapping(string adoStatus, bool isDraft, string expected)
    {
        Assert.Equal(expected, AzureDevOpsPullRequestDiscoveryTool.MapAdoStatusToStatus(adoStatus, isDraft));
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/project/_git/MyRepo", "org", "project", "MyRepo")]
    [InlineData("https://dev.azure.com/contoso/webapp/_git/FrontEnd", "contoso", "webapp", "FrontEnd")]
    public void TryParseAzureDevOpsRepositoryUrl_ParsesValidUrl(
        string url, string expectedOrg, string expectedProject, string expectedRepo)
    {
        var result = AzureDevOpsPullRequestDiscoveryTool.TryParseAzureDevOpsRepositoryUrl(url);
        Assert.NotNull(result);
        Assert.Equal(expectedOrg, result.Value.Org);
        Assert.Equal(expectedProject, result.Value.Project);
        Assert.Equal(expectedRepo, result.Value.RepoName);
    }

    [Theory]
    [InlineData("https://github.com/org/repo")]
    [InlineData("https://dev.azure.com/org")]
    [InlineData("https://dev.azure.com/org/project")]
    [InlineData("https://dev.azure.com/org/project/repo")]
    public void TryParseAzureDevOpsRepositoryUrl_ReturnsNullForInvalidUrl(string url)
    {
        Assert.Null(AzureDevOpsPullRequestDiscoveryTool.TryParseAzureDevOpsRepositoryUrl(url));
    }
}
