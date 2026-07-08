using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools.AzureDevOps;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class AzureDevOpsRepositoryDiscoveryToolTests
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

    private static EntitySnapshot CreateAzureDevOpsProjectEntity(string entityId, string projectUrl)
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
    public async Task AzureDevOpsRepositoryDiscoveryTool_UpsertsRepository()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var projectEntityId = "11111111-1111-1111-1111-111111111111";
        var projectEntity = CreateAzureDevOpsProjectEntity(
            projectEntityId,
            "https://dev.azure.com/myorg/myproject");
        var context = CreateContext(dataAccessLayer, projectEntity);

        var repoResponse = """
            {
              "value": [
                {
                  "id": "aaaabbbb-aaaa-bbbb-cccc-111122223333",
                  "name": "MyRepo",
                  "remoteUrl": "https://myorg@dev.azure.com/myorg/myproject/_git/MyRepo"
                }
              ]
            }
            """;

        var tool = new AzureDevOpsRepositoryDiscoveryTool(
            httpGetter: (url, pat, ct) => Task.FromResult(repoResponse));

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("azure-devops", "myorg", "myproject", "MyRepo"));
        Assert.NotNull(entity);

        var raw = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"azure-devops-repository\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"git-repository\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"aaaabbbb-aaaa-bbbb-cccc-111122223333\"", raw, StringComparison.Ordinal);
        Assert.Contains(projectEntityId, raw, StringComparison.Ordinal);
        Assert.Contains("MyRepo", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsRepositoryDiscoveryTool_SkipsNonProjectParticipants()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var nonProjectEntity = CreateDummyEntity(
            new EntityId("22222222-2222-2222-2222-222222222222"), "not-a-project");
        var context = CreateContext(dataAccessLayer, nonProjectEntity);

        var callCount = 0;
        var tool = new AzureDevOpsRepositoryDiscoveryTool(
            httpGetter: (url, pat, ct) =>
            {
                callCount++;
                return Task.FromResult("{\"value\":[]}");
            });

        var result = await tool.ExecuteAsync(context);

        Assert.Equal(0, callCount);
        Assert.Contains("0 project(s)", result.ResultContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureDevOpsRepositoryDiscoveryTool_SetsUrlToAzureDevOpsWebUrl()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var projectEntity = CreateAzureDevOpsProjectEntity(
            "33333333-3333-3333-3333-333333333333",
            "https://dev.azure.com/contoso/myproj");
        var context = CreateContext(dataAccessLayer, projectEntity);

        var repoResponse = """
            {
              "value": [
                {
                  "id": "repo-id-001",
                  "name": "WebApp",
                  "remoteUrl": "https://contoso@dev.azure.com/contoso/myproj/_git/WebApp"
                }
              ]
            }
            """;

        var tool = new AzureDevOpsRepositoryDiscoveryTool(
            httpGetter: (url, pat, ct) => Task.FromResult(repoResponse));

        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("azure-devops", "contoso", "myproj", "WebApp"));
        Assert.NotNull(entity);

        var raw = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("https://dev.azure.com/contoso/myproj/_git/WebApp", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractProjectInfo_ReturnNullForNonProjectEntity()
    {
        using var doc = JsonDocument.Parse("""
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "note"],
              "names": [["notes", "mynote"]]
            }
            """);
        var entity = new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ModifiedTime = new Timestamp(),
            Relationships = [],
            Data = doc.RootElement.Clone(),
        };

        Assert.Null(AzureDevOpsRepositoryDiscoveryTool.TryExtractProjectInfo(entity));
    }
}
