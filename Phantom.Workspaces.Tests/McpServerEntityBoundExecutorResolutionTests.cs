using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport.Mcp;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Verifies issue #1439 (per-component-executor-binding, Commit 7): <c>mcp-server-entity</c> resolution
/// is scoped to the BOUND executor's machine. The ordered search prefixes built by
/// <see cref="ToolResourceFactory.CreateMcpServerSearchPrefixes"/> place the bound machine's profile
/// FIRST, then <c>${USER}/mcp-servers</c>, then <c>defaults/mcp-servers</c>; and #1438's
/// <see cref="RemoteMcpHostHandler"/> resolves a stored tool reference through that scoped resolver on
/// the host, so the bound machine's registration — not the resolving instance's — wins.
/// </summary>
public sealed class McpServerEntityBoundExecutorResolutionTests
{
    private const string UserName = "test-user";

    private static ToolResource McpServerResource(string name) => new()
    {
        Kind = "tool",
        Id = McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
        Name = name,
    };

    [Fact]
    public void CreateMcpServerSearchPrefixes_OrdersBoundMachineProfileFirst()
    {
        var prefixes = ToolResourceFactory.CreateMcpServerSearchPrefixes(UserName, "machine-b");

        Assert.Equal(3, prefixes.Count);
        // Machine profile prefix first, embedding the bound user + host, then ${USER}, then defaults.
        Assert.Equal("machine-b", prefixes[0].Components[6]);
        Assert.Equal([WorkspaceEntityMetaVariables.User, "mcp-servers"], prefixes[1].Components);
        Assert.Equal(["defaults", "mcp-servers"], prefixes[2].Components);
    }

    [Fact]
    public async Task Resolve_BoundExecutorMachineProfile_WinsOverUserAndDefaults()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var prefixes = ToolResourceFactory.CreateMcpServerSearchPrefixes(UserName, "machine-b");

        await StoreMcpServerAsync(dataAccessLayer, [.. prefixes[0].Components, "github"], "github", "https://machine-b.example/mcp/");
        await StoreMcpServerAsync(dataAccessLayer, [.. prefixes[1].Components, "github"], "github", "https://user.example/mcp/");
        await StoreMcpServerAsync(dataAccessLayer, [.. prefixes[2].Components, "github"], "github", "https://defaults.example/mcp/");
        var factory = new McpServerEntityToolResourceFactory(dataAccessLayer, prefixes);

        var tool = await factory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
        Assert.Equal("https://machine-b.example/mcp/", Assert.IsType<ApiKeyConnection>(mcpTool.Connection).Endpoint);
    }

    [Fact]
    public async Task Resolve_FallsBackThroughUserThenDefaults()
    {
        var prefixes = ToolResourceFactory.CreateMcpServerSearchPrefixes(UserName, "machine-b");

        // No machine-profile registration: resolution falls back to ${USER}/mcp-servers.
        var withUser = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(withUser, [.. prefixes[1].Components, "github"], "github", "https://user.example/mcp/");
        await StoreMcpServerAsync(withUser, [.. prefixes[2].Components, "github"], "github", "https://defaults.example/mcp/");
        var userTool = await new McpServerEntityToolResourceFactory(withUser, prefixes)
            .ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);
        Assert.Equal("https://user.example/mcp/", Assert.IsType<ApiKeyConnection>(Assert.IsAssignableFrom<McpTool>(userTool).Connection).Endpoint);

        // No machine or user registration: resolution falls back to defaults/mcp-servers.
        var withDefaultsOnly = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(withDefaultsOnly, [.. prefixes[2].Components, "github"], "github", "https://defaults.example/mcp/");
        var defaultsTool = await new McpServerEntityToolResourceFactory(withDefaultsOnly, prefixes)
            .ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);
        Assert.Equal("https://defaults.example/mcp/", Assert.IsType<ApiKeyConnection>(Assert.IsAssignableFrom<McpTool>(defaultsTool).Connection).Endpoint);
    }

    [Fact]
    public async Task Resolve_UsesBoundExecutorContext_NotResolvingInstance()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var boundPrefixes = ToolResourceFactory.CreateMcpServerSearchPrefixes(UserName, "bound-machine");
        var resolvingInstancePrefixes = ToolResourceFactory.CreateMcpServerSearchPrefixes(UserName, "resolving-instance");

        // The same logical name is registered on two different machines' profiles.
        await StoreMcpServerAsync(dataAccessLayer, [.. boundPrefixes[0].Components, "github"], "github", "https://bound.example/mcp/");
        await StoreMcpServerAsync(dataAccessLayer, [.. resolvingInstancePrefixes[0].Components, "github"], "github", "https://resolving.example/mcp/");

        // A factory scoped to the BOUND executor resolves the bound machine's entity, not the resolving
        // instance's — proving resolution uses the bound executor's context.
        var boundTool = await new McpServerEntityToolResourceFactory(dataAccessLayer, boundPrefixes)
            .ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);
        Assert.Equal("https://bound.example/mcp/", Assert.IsType<ApiKeyConnection>(Assert.IsAssignableFrom<McpTool>(boundTool).Connection).Endpoint);
    }

    [Fact]
    public async Task Resolve_ViaRemoteHostHandler_UsesMachinePrefixFirst()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var prefixes = ToolResourceFactory.CreateMcpServerSearchPrefixes(UserName, "bound-machine");

        // The bound machine's profile AND the global defaults both register 'github'; the machine
        // profile must win when the reference is resolved on the host through the scoped resolver.
        await StoreMcpServerAsync(dataAccessLayer, [.. prefixes[0].Components, "github"], "github", "https://bound.example/mcp/");
        await StoreMcpServerAsync(dataAccessLayer, [.. prefixes[2].Components, "github"], "github", "https://defaults.example/mcp/");

        var services = new AgentServices
        {
            ToolResourceFactory = new McpServerEntityToolResourceFactory(dataAccessLayer, prefixes),
        };
        var handler = new RemoteMcpHostHandler(services);

        var request = McpConnectionRequest.FromToolReference(
            McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
            "github");
        var tool = await handler.ResolveConnectionAsync(request, TestContext.Current.CancellationToken);

        var mcpTool = Assert.IsAssignableFrom<McpTool>(tool);
        Assert.Equal("https://bound.example/mcp/", Assert.IsType<ApiKeyConnection>(mcpTool.Connection).Endpoint);
    }

    [Fact]
    public async Task Resolve_ViaRemoteHostHandler_WithoutScopedResolver_ReturnsNull()
    {
        // No AgentServices.ToolResourceFactory: a tool reference cannot be resolved, so the host
        // declines it rather than hosting an unrelated server.
        var handler = new RemoteMcpHostHandler(new AgentServices());

        var request = McpConnectionRequest.FromToolReference(
            McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
            "github");
        var tool = await handler.ResolveConnectionAsync(request, TestContext.Current.CancellationToken);

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
