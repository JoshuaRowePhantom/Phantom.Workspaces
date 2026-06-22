using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Tests;

public sealed class McpServerEntityToolResourceRepositoryTests
{
    [AvaloniaFact]
    public async Task ToolResources_ReflectExistingMcpServerEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        await AddMcpServerAsync(broker, "a0a0a0a0-0000-0000-0000-000000000001", ["defaults", "mcp-servers", "github"], "github", ct);

        var repository = await McpServerEntityToolResourceRepository.CreateAsync(broker, ct);

        var resource = Assert.Single(repository.ToolResources);
        Assert.Equal("github", resource.Name);
        Assert.Equal(McpServerEntityToolResourceFactory.McpServerEntityToolResourceId, resource.Id);
    }

    [AvaloniaFact]
    public async Task ToolResources_UpdateWhenMcpServerEntityAdded()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var repository = await McpServerEntityToolResourceRepository.CreateAsync(broker, ct);
        Assert.DoesNotContain(repository.ToolResources, static resource => resource.Name == "custom");

        await AddMcpServerAsync(broker, "a0a0a0a0-0000-0000-0000-000000000002", ["defaults", "mcp-servers", "custom"], "custom", ct);

        Assert.Contains(repository.ToolResources, static resource => resource.Name == "custom");
    }

    [AvaloniaFact]
    public async Task ToolResources_DeduplicateByServerName()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        await AddMcpServerAsync(broker, "a0a0a0a0-0000-0000-0000-000000000003", ["defaults", "mcp-servers", "github"], "github", ct);
        await AddMcpServerAsync(broker, "a0a0a0a0-0000-0000-0000-000000000004", ["user-computer-profiles", "this-machine", "copilot", "mcp-servers", "github"], "github", ct);

        var repository = await McpServerEntityToolResourceRepository.CreateAsync(broker, ct);

        Assert.Single(repository.ToolResources);
        Assert.Equal("github", repository.ToolResources[0].Name);
    }

    private static async Task AddMcpServerAsync(
        EntityBroker broker,
        string entityId,
        string[] name,
        string serverName,
        CancellationToken cancellationToken)
    {
        var data = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["mcp-server"],
              "names": [{{JsonSerializer.Serialize(name)}}],
              "mcp-server": {
                "serverName": {{JsonSerializer.Serialize(serverName)}},
                "connection": {
                  "kind": "key",
                  "endpoint": "https://api.githubcopilot.com/mcp/",
                  "apiKey": "${GITHUB_TOKEN}"
                },
                "approvalMode": { "kind": "never" }
              }
            }
            """).RootElement.Clone();

        var updateResult = await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Add mcp-server entity." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(entityId),
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = data,
                    },
                ],
            },
            cancellationToken);

        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }
}
