using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Tools.AzureDevOps;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class AzureDevOpsWorkItemDiscoveryToolTests
{
    private static JsonElement CreateAzureDevOpsProjectEntity(string entityId, string projectUrl)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "repository", "azure-devops-project", "external"],
              "names": [["azure-devops", "myorg", "myproject"]],
              "display-name": {"default": "My Project"},
              "urls": {"default": "{{projectUrl}}"}
            }
            """);
        return document.RootElement.Clone();
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

    private static Task<string> MakeWiqlAndBatchResponder(
        string wiqlResponse,
        string batchResponse)
    {
        // Returns a function that responds to both the WIQL and batch requests by URL pattern
        return Task.FromResult(string.Empty);
    }

    [Fact]
    public async Task AzureDevOpsWorkItemDiscoveryTool_MapsTagsToLabels()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var projectEntity = CreateAzureDevOpsProjectEntity(
            "11111111-1111-1111-1111-111111111111",
            "https://dev.azure.com/myorg/myproject");
        var context = await ValidatedWorkspaceToolTestContext.CreateAsync(fixture, projectEntity);

        var wiqlResponse = """{"workItems": [{"id": 99}]}""";
        var batchResponse = """
            {
              "value": [
                {
                  "id": 99,
                  "fields": {
                    "System.Title": "Fix performance",
                    "System.State": "Active",
                    "System.Tags": "bug;performance"
                  },
                  "_links": {"html": {"href": "https://dev.azure.com/myorg/myproject/_workitems/edit/99"}}
                }
              ]
            }
            """;

        var callCount = 0;
        var tool = new AzureDevOpsWorkItemDiscoveryTool(httpPoster: (url, body, ct) =>
        {
            callCount++;
            return Task.FromResult(callCount == 1 ? wiqlResponse : batchResponse);
        });

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "work-items", "99"));
        Assert.NotNull(entity);

        var raw = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"bug\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"performance\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"in-progress\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsWorkItemDiscoveryTool_MapsGitCommitLinksToRelatedCommits()
    {
        const string commitSha = "abc1234567890abcdef1234567890abcdef123456";
        const string projectId = "proj-guid-1111-1111-1111-111111111111";
        const string repoId = "repo-guid-2222-2222-2222-222222222222";

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var projectEntity = CreateAzureDevOpsProjectEntity(
            "22222222-2222-2222-2222-222222222222",
            "https://dev.azure.com/myorg/myproject");
        var context = await ValidatedWorkspaceToolTestContext.CreateAsync(fixture, projectEntity);

        var wiqlResponse = """{"workItems": [{"id": 77}]}""";
        var vstfsUrl = $"vstfs:///Git/Commit/{projectId}%2F{repoId}%2F{commitSha}";
        var batchResponse = $$"""
            {
              "value": [
                {
                  "id": 77,
                  "fields": {
                    "System.Title": "Commit linked item",
                    "System.State": "New",
                    "System.Tags": ""
                  },
                  "relations": [
                    {
                      "rel": "ArtifactLink",
                      "url": "{{vstfsUrl}}",
                      "attributes": {"name": "Fixed in Commit"}
                    }
                  ],
                  "_links": {"html": {"href": "https://dev.azure.com/myorg/myproject/_workitems/edit/77"} }
                }
              ]
            }
            """;

        var callCount = 0;
        var tool = new AzureDevOpsWorkItemDiscoveryTool(httpPoster: (url, body, ct) =>
        {
            callCount++;
            return Task.FromResult(callCount == 1 ? wiqlResponse : batchResponse);
        });

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "work-items", "77"));
        Assert.NotNull(entity);

        var raw = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains(commitSha, raw, StringComparison.Ordinal);
        Assert.Contains("\"open\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsWorkItemDiscoveryTool_MapsStatesToStatus()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var projectEntity = CreateAzureDevOpsProjectEntity(
            "33333333-3333-3333-3333-333333333333",
            "https://dev.azure.com/myorg/myproject");
        var context = await ValidatedWorkspaceToolTestContext.CreateAsync(fixture, projectEntity);

        var wiqlResponse = """{"workItems": [{"id": 1}, {"id": 2}, {"id": 3}]}""";
        var batchResponse = """
            {
              "value": [
                {
                  "id": 1,
                  "fields": {"System.Title": "New Item", "System.State": "New", "System.Tags": ""},
                  "_links": {"html": {"href": "https://dev.azure.com/myorg/myproject/_workitems/edit/1"}}
                },
                {
                  "id": 2,
                  "fields": {"System.Title": "Active Item", "System.State": "In Progress", "System.Tags": ""},
                  "_links": {"html": {"href": "https://dev.azure.com/myorg/myproject/_workitems/edit/2"}}
                },
                {
                  "id": 3,
                  "fields": {"System.Title": "Done Item", "System.State": "Resolved", "System.Tags": ""},
                  "_links": {"html": {"href": "https://dev.azure.com/myorg/myproject/_workitems/edit/3"}}
                }
              ]
            }
            """;

        var callCount = 0;
        var tool = new AzureDevOpsWorkItemDiscoveryTool(httpPoster: (url, body, ct) =>
        {
            callCount++;
            return Task.FromResult(callCount == 1 ? wiqlResponse : batchResponse);
        });

        await tool.ExecuteAsync(context);

        var item1 = await GetEntityByNameAsync(dataAccessLayer, new EntityName("azure-devops", "myorg", "myproject", "work-items", "1"));
        var item2 = await GetEntityByNameAsync(dataAccessLayer, new EntityName("azure-devops", "myorg", "myproject", "work-items", "2"));
        var item3 = await GetEntityByNameAsync(dataAccessLayer, new EntityName("azure-devops", "myorg", "myproject", "work-items", "3"));

        Assert.NotNull(item1);
        Assert.NotNull(item2);
        Assert.NotNull(item3);
        Assert.Contains("\"open\"", item1.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"in-progress\"", item2.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"closed\"", item3.Data?.GetRawText() ?? string.Empty, StringComparison.Ordinal);
    }
}
