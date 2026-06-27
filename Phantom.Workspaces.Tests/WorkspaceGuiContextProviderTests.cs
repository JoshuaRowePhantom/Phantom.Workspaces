using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceGuiContextProviderTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceList_ReturnsAllWorkspacePanes_WithCorrectIsSelectedFlag()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("aaaa0001-aaaa-4aaa-aaaa-aaaaaaaaaaaa");
        var workspaceIdB = new EntityId("bbbb0002-bbbb-4bbb-bbbb-bbbbbbbbbbbb");

        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA, """
            {
              "entity-id": "aaaa0001-aaaa-4aaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "list-a"]],
              "display-name": { "default": "List Workspace A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB, """
            {
              "entity-id": "bbbb0002-bbbb-4bbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "list-b"]],
              "display-name": { "default": "List Workspace B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Select workspace B
        viewModel.SelectedWorkspacePane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdB.ToString());

        var tool = await GetToolAsync(viewModel, "workspace_list");
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var panes = resultJson.EnumerateArray().ToList();

        var paneA = Assert.Single(panes, p => p.GetProperty("workspace_entity_id").GetString() == workspaceIdA.ToString());
        Assert.Equal("List Workspace A", paneA.GetProperty("title").GetString());
        Assert.False(paneA.GetProperty("is_selected").GetBoolean());

        var paneB = Assert.Single(panes, p => p.GetProperty("workspace_entity_id").GetString() == workspaceIdB.ToString());
        Assert.Equal("List Workspace B", paneB.GetProperty("title").GetString());
        Assert.True(paneB.GetProperty("is_selected").GetBoolean());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabList_WithNoWorkspaceEntityId_ReturnsTabsForSelectedPane()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tablist-tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tablist-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var tool = await GetToolAsync(viewModel, "tab_list");
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabIds = resultJson.EnumerateArray()
            .Select(t => t.GetProperty("tab_id").GetString())
            .ToArray();

        Assert.Contains("tablist-tab-a", tabIds);
        Assert.Contains("tablist-tab-b", tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabList_WithNoWorkspaceEntityId_MarksActiveTabCorrectly()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "active-tab-a", Title = "Active Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "active-tab-b", Title = "Active Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // tabB is active after opening

        var tool = await GetToolAsync(viewModel, "tab_list");
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabs = resultJson.EnumerateArray().ToList();

        var listedTabB = Assert.Single(tabs, t => t.GetProperty("tab_id").GetString() == "active-tab-b");
        Assert.True(listedTabB.GetProperty("is_active").GetBoolean());

        var listedTabA = Assert.Single(tabs, t => t.GetProperty("tab_id").GetString() == "active-tab-a");
        Assert.False(listedTabA.GetProperty("is_active").GetBoolean());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabList_WithWorkspaceEntityId_ReturnsTabsForThatPane()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("cccc0003-cccc-4ccc-cccc-cccccccccccc");

        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, """
            {
              "entity-id": "cccc0003-cccc-4ccc-cccc-cccccccccccc",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tablist-specific"]],
              "display-name": { "default": "Tab List Specific Workspace" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        // Add a tab to the specific workspace pane by selecting it first
        viewModel.SelectedWorkspacePane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceId.ToString());
        var tab = new WebViewModel("https://specific.example.com") { Id = "specific-pane-tab", Title = "Specific Pane Tab" };
        await viewModel.OpenTabAsync(tab);

        // Switch back to default pane
        viewModel.SelectedWorkspacePane = viewModel.WorkspacePanes.First(p => p.Id != workspaceId.ToString());

        var tool = await GetToolAsync(viewModel, "tab_list");
        var idArg = JsonDocument.Parse($"\"{workspaceId}\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["workspace_entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabIds = resultJson.EnumerateArray()
            .Select(t => t.GetProperty("tab_id").GetString())
            .ToArray();

        Assert.Contains("specific-pane-tab", tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabList_WithUnknownWorkspaceEntityId_ReturnsError()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "tab_list");
        var idArg = JsonDocument.Parse("\"dddddddd-dddd-4ddd-dddd-dddddddddddd\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["workspace_entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    private static async Task<AIFunction> GetToolAsync(MainWindowViewModel viewModel, string toolName)
    {
        var context = new WorkspaceGuiContext
        {
            MainWindowViewModel = viewModel,
            ShortcutManager = new ShortcutManager(),
        };
        var provider = new WorkspaceGuiContextProvider(context);
        var tools = await GetToolsAsync(provider);
        return (AIFunction)tools.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
    }

    private static async Task<AITool[]> GetToolsAsync(WorkspaceGuiContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }

    private static async Task<SubscribedEntityViewModel> UpsertEntityAndLoadAsync(
        EntityBroker entityBroker,
        EntityId entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await entityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Add test workspace." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });
        var entityResult = Assert.Single(updateResult.EntityResults, r => r.RequestedEntityId == entityId);
        Assert.NotEqual(UpdateState.Failed, entityResult.UpdateState);
        return Assert.Single(await entityBroker.GetEntitiesAsync([entityId]));
    }
}
