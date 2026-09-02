using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

// Issue #1236: get_current_session returned empty {} on Copilot / running-agent sessions because
// the GUI shortcut path constructed a session-id-only context. The fix routes the GUI path through
// the shared CurrentSessionContextFactory. This test asserts the GUI shortcut-context path delegates
// to that shared factory so the resolved user / computer / profile identity matches exactly, rather
// than resolving identity via a separate code path.
public sealed class AgentSessionShortcutContextTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentSessionShortcutContext_BuildCurrentSessionContext_DelegatesToSharedFactory()
    {
        var userName = Environment.UserName;
        var computerName = Environment.MachineName;

        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();

        // InitializeAsync bootstraps the current user / computer / user-computer-profile entities
        // (no profile override), so the shared factory has real host-identity entities to resolve.
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var dataAccessLayer = entityBroker.EntityRepository.DataAccessLayer;

        var shortcutContext = new AgentSessionShortcutContext();

        var services = await shortcutContext.CreateAgentServicesAsync(viewModel);

        // The GUI path stashes the resolved host context on the returned AgentServices (issue #1236).
        var actual = Assert.IsType<CurrentSessionContext>(services.CurrentSessionContext);

        Assert.Equal(string.Empty, actual.AgentSessionId);
        Assert.NotNull(actual.User);
        Assert.NotNull(actual.Computer);
        Assert.NotNull(actual.UserComputerProfile);

        // Prove delegation: the GUI path must produce exactly what the shared factory produces for
        // the same host identity (same session id, same resolved user / computer / profile entities).
        var expected = await CurrentSessionContextFactory.CreateForHostAsync(
            agentSessionId: string.Empty,
            dataAccessLayer: dataAccessLayer,
            userName: userName,
            computerName: computerName,
            effectiveComputerName: computerName,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(expected.User);
        Assert.NotNull(expected.Computer);
        Assert.NotNull(expected.UserComputerProfile);
        Assert.Equal(expected.User!.EntityId, actual.User!.EntityId);
        Assert.Equal(expected.Computer!.EntityId, actual.Computer!.EntityId);
        Assert.Equal(expected.UserComputerProfile!.EntityId, actual.UserComputerProfile!.EntityId);
    }

    // Issue #1399: MCP servers created through the UI are named under the mcp-server entity-type's
    // default creation location (${USER}/mcp-servers/<name>), but the tool-resource resolver only
    // searched the machine profile and defaults/mcp-servers, so those servers were unresolvable.
    // This guards that CreateToolResourceFactory now searches the ${USER}/mcp-servers prefix: an
    // mcp-server entity created at that default location resolves through the factory built for a
    // GUI session.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentSessionShortcutContext_ToolResourceFactory_IncludesUserMcpServersPrefix()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var dataAccessLayer = entityBroker.EntityRepository.DataAccessLayer;
        var workspaceEntitySession = entityBroker.EntityRepository.WorkspaceEntitySession;

        // Name the server the way the create flow does: via the mcp-server entity-type's
        // default-name-prefixes, which bind ${USER} to the concrete user prefix.
        var names = await WorkspaceEntityNameFactory.CreateEntityNames(
            dataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("mcp-server"),
            "issue1399-server",
            CancellationToken.None);

        // Guard the premise: the resolved name must actually be under the user mcp-servers location
        // (not a bare fallback), otherwise the test would not exercise the ${USER} prefix.
        Assert.Contains(names, name => name.Components.Contains("mcp-servers"));

        var entityId = Guid.NewGuid().ToString("D");
        var entity = new JsonObject
        {
            ["entity-id"] = entityId,
            ["entity-types"] = new JsonArray("entity", "mcp-server"),
            ["names"] = new JsonArray(
                names.Select(name => (JsonNode)new JsonArray(
                    name.Components.Select(component => (JsonNode)JsonValue.Create(component)).ToArray()))
                    .ToArray()),
            ["mcp-server"] = new JsonObject
            {
                ["serverName"] = "issue1399-server",
                ["connection"] = new JsonObject
                {
                    ["kind"] = "key",
                    ["endpoint"] = "https://user-created.example/mcp/",
                    ["apiKey"] = "${GITHUB_TOKEN}",
                },
                ["approvalMode"] = new JsonObject { ["kind"] = "never" },
            },
        };

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed user-scoped mcp-server entity." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(entityId),
                        Data = JsonSerializer.Deserialize<JsonElement>(entity.ToJsonString()),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            CancellationToken.None);
        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        var shortcutContext = new AgentSessionShortcutContext();
        var services = await shortcutContext.CreateAgentServicesAsync(viewModel);
        Assert.NotNull(services.ToolResourceFactory);

        var tool = await services.ToolResourceFactory!.ResolveToolResourceAsync(
            new ToolResource
            {
                Kind = "tool",
                Id = McpServerEntityToolResourceFactory.McpServerEntityToolResourceId,
                Name = "issue1399-server",
            },
            CancellationToken.None);

        var mcpTool = Assert.IsType<McpTool>(tool);
        Assert.Equal("issue1399-server", mcpTool.ServerName);
        var connection = Assert.IsType<ApiKeyConnection>(mcpTool.Connection);
        Assert.Equal("https://user-created.example/mcp/", connection.Endpoint);
    }
}
