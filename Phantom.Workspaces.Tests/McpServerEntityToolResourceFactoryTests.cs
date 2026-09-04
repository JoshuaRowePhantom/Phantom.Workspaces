using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Tests;

public sealed class McpServerEntityToolResourceFactoryTests
{
    private static readonly EntityName LocalPrefix =
        new("user-computer-profiles", "this-machine", "copilot", "mcp-servers");

    private static readonly EntityName GlobalPrefix =
        new("defaults", "mcp-servers");

    // ${USER}/mcp-servers is the mcp-server entity-type's default creation location; the session
    // data-access layer binds ${USER} to a concrete user prefix. These tests use a fixed concrete
    // user prefix to exercise the factory's prefix search/precedence directly (issue #1399).
    private static readonly EntityName UserPrefix =
        new("users", "username", "test-user", "mcp-servers");

    private static ToolResource McpServerResource(string name) => new()
    {
        Kind = "tool",
        Id = McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
        Name = name,
    };

    [Fact]
    public async Task McpServerEntityToolResourceFactory_EntityUnderUserPrefix_Resolves()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. UserPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://user.example/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [LocalPrefix, UserPrefix, GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
        var connection = Assert.IsType<ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://user.example/mcp/", connection.Endpoint);
    }

    [Fact]
    public async Task McpServerEntityToolResourceFactory_UserPrefixTakesPrecedenceOverDefaults()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. GlobalPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://global.example/mcp/");
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. UserPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://user.example/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [LocalPrefix, UserPrefix, GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
        var connection = Assert.IsType<ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://user.example/mcp/", connection.Endpoint);
    }

    [Fact]
    public async Task McpServerEntityToolResourceFactory_MachineProfileTakesPrecedenceOverUserPrefix()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. UserPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://user.example/mcp/");
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. LocalPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://local.example/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [LocalPrefix, UserPrefix, GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
        var connection = Assert.IsType<ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://local.example/mcp/", connection.Endpoint);
    }

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

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
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

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
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

    [Fact]
    public async Task ResolveToolResourceAsync_WithTypeField_ProducesPhantomMcpToolWithTransport()
    {
        // #1416 point A: an mcp-server entity carrying "type": "sse" must resolve to a PhantomMcpTool
        // whose Transport is Sse (the raw 'type' is read before AgentSchema drops it).
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. GlobalPrefix.Components, "bluebird"],
            serverName: "bluebird",
            endpoint: "https://mcp.bluebird-ai.net/",
            type: "sse");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("bluebird"), TestContext.Current.CancellationToken);

        var phantomTool = Assert.IsType<PhantomMcpTool>(tool);
        Assert.Equal(McpHttpTransport.Sse, phantomTool.Transport);
    }

    [Fact]
    public async Task ResolveToolResourceAsync_WithoutTypeField_DefaultsToStreamable()
    {
        // #1416: with no 'type', resolution must still produce a PhantomMcpTool that defaults to
        // Streamable HTTP (never AutoDetect).
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(
            dataAccessLayer,
            [.. GlobalPrefix.Components, "github"],
            serverName: "github",
            endpoint: "https://api.githubcopilot.com/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, [GlobalPrefix]);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var phantomTool = Assert.IsType<PhantomMcpTool>(tool);
        Assert.Equal(McpHttpTransport.Streamable, phantomTool.Transport);
    }

    private static async Task StoreMcpServerAsync(
        IDataAccessLayer dataAccessLayer,
        string[] entityName,
        string serverName,
        string endpoint,
        string? type = null)
    {
        var typeLine = type is null
            ? string.Empty
            : ",\n                \"type\": " + JsonSerializer.Serialize(type);
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
                "approvalMode": { "kind": "never" }{{typeLine}}
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

