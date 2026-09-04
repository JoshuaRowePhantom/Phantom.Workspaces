using System;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

// Issue #1403: the MCP tool-resource composition (fixed built-ins + mcp-server-entity resolution
// with the machine > ${USER}/mcp-servers > defaults/mcp-servers precedence) was moved out of the
// GUI AgentSessionShortcutContext into the Services-layer ToolResourceFactory. This guards that the
// extracted factory preserves the issue #1399 precedence ordering.
public sealed class ToolResourceFactoryTests
{
    private const string UserName = "test-user";
    private const string ComputerName = "test-machine";

    private static readonly string[] MachinePrefix =
    [
        "computer-user-profiles", "users", "username", UserName,
        "computers", "hostname", ComputerName, "copilot", "mcp-servers",
    ];

    private static readonly string[] UserPrefix = ["${USER}", "mcp-servers"];
    private static readonly string[] DefaultsPrefix = ["defaults", "mcp-servers"];

    private static ToolResource McpServerResource(string name) => new()
    {
        Kind = "tool",
        Id = McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
        Name = name,
    };

    [Fact]
    public async Task ToolResourceFactory_CreateMcpServerResolution_UsesMachineUserDefaultsPrecedence()
    {
        // Machine registration must win over both the user default location and global defaults.
        var machineWins = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(machineWins, [.. DefaultsPrefix, "github"], "github", "https://global.example/mcp/");
        await StoreMcpServerAsync(machineWins, [.. UserPrefix, "github"], "github", "https://user.example/mcp/");
        await StoreMcpServerAsync(machineWins, [.. MachinePrefix, "github"], "github", "https://machine.example/mcp/");

        var machineFactory = ToolResourceFactory.CreateMcpServerResolution(machineWins, UserName, ComputerName);
        var machineTool = await machineFactory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);
        var machineConnection = Assert.IsType<ApiKeyConnection>(Assert.IsAssignableFrom<McpTool>(machineTool).Connection);
        Assert.Equal("https://machine.example/mcp/", machineConnection.Endpoint);

        // With no machine registration, the ${USER}/mcp-servers location must win over global defaults.
        var userWins = new InMemoryDataAccessLayer();
        await StoreMcpServerAsync(userWins, [.. DefaultsPrefix, "github"], "github", "https://global.example/mcp/");
        await StoreMcpServerAsync(userWins, [.. UserPrefix, "github"], "github", "https://user.example/mcp/");

        var userFactory = ToolResourceFactory.CreateMcpServerResolution(userWins, UserName, ComputerName);
        var userTool = await userFactory.ResolveToolResourceAsync(McpServerResource("github"), TestContext.Current.CancellationToken);
        var userConnection = Assert.IsType<ApiKeyConnection>(Assert.IsAssignableFrom<McpTool>(userTool).Connection);
        Assert.Equal("https://user.example/mcp/", userConnection.Endpoint);
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
            System.Threading.CancellationToken.None);

        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }
}

