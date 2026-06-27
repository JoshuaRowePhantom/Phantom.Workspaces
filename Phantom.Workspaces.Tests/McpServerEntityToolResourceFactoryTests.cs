using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Tests;

public sealed class McpServerEntityToolResourceFactoryTests
{
    private static readonly EntityName LocalPrefix =
        new("user-computer-profiles", "this-machine", "copilot", "mcp-servers");

    private static readonly EntityName GlobalPrefix =
        new("defaults", "mcp-servers");

    private static ToolResource McpServerResource(string name) => new()
    {
        Kind = "tool",
        Id = McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
        Name = name,
    };

    [Fact]
    public async Task ResolveToolResourceAsync_ResolvesGlobalMcpServerEntity()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. GlobalPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://api.githubcopilot.com/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [LocalPrefix, GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsType<McpTool>(tool);
        Assert.Equal("github", mcpTool.ServerName);
        var connection = Assert.IsType<ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://api.githubcopilot.com/mcp/", connection.Endpoint);
    }

    [Fact]
    public async Task ResolveToolResourceAsync_PrefersLocalOverGlobal()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. GlobalPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://global.example/mcp/");
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. LocalPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://local.example/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [LocalPrefix, GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsType<McpTool>(tool);
        var connection = Assert.IsType<ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://local.example/mcp/", connection.Endpoint);
    }

    [Fact]
    public async Task ResolveToolResourceAsync_WhenEntityMissing_ReturnsNull()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [LocalPrefix, GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("nonexistent"), TestContext.Current.CancellationToken);

        Assert.Null(tool);
    }

    [Fact]
    public async Task ResolveToolResourceAsync_WhenIdIsNotMcpServerEntity_ReturnsNull()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(new ToolResource
        {
            Kind = "tool",
            Id = "fixed",
            Name = "github",
        }, TestContext.Current.CancellationToken);

        Assert.Null(tool);
    }

    private static async Task StoreMcpServerAsync(
        IDataAccessLayer dataAccessLayer,
        string[] entityName,
        string serverName,
        string endpoint)
    {
        using var jsonDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{Guid.NewGuid():D}}",
              "entity-types": ["entity", "mcp-server"],
              "names": [{{JsonSerializer.Serialize(entityName)}}],
              "mcp-server": {
                "serverName": {{JsonSerializer.Serialize(serverName)}},
                "connection": {
                  "kind": "key",
                  "endpoint": {{JsonSerializer.Serialize(endpoint)}},
                  "apiKey": "${GITHUB_TOKEN}"
                },
                "approvalMode": { "kind": "never" }
              }
            }
            """);
        var entityData = jsonDocument.RootElement.Clone();

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed mcp-server entity." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(entityData.GetProperty("entity-id").GetString()!),
                        Data = entityData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            CancellationToken.None);

        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }
}
