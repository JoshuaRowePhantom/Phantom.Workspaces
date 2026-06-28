using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Shell;
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

    // ── open_tab tests ────────────────────────────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Entity_OpensEntityTab()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("ee000001-ee00-4ee0-ee00-ee0000000001");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "task"],
              "names": [["tests", "open-tab", "entity-1"]],
              "display-name": { "default": "Open Tab Entity 1" }
            }
            """);

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"entity\"").RootElement.Clone();
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = targetArg, ["entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabId = resultJson.GetProperty("tab_id").GetString();
        Assert.Equal(entityId.ToString(), tabId);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var entityDoc = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(doc => string.Equals(doc.Id, entityId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(entityDoc);
        Assert.IsType<EntityWorkspaceTabViewModel>(entityDoc!.TabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Entity_DuplicateActivatesExisting()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("ee000002-ee00-4ee0-ee00-ee0000000002");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "task"],
              "names": [["tests", "open-tab", "entity-2"]],
              "display-name": { "default": "Open Tab Entity 2" }
            }
            """);

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"entity\"").RootElement.Clone();
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var args = new Dictionary<string, object?> { ["target"] = targetArg, ["entity_id"] = idArg };

        var result1 = Assert.IsType<JsonElement>(await tool.InvokeAsync(
            new AIFunctionArguments(args), CancellationToken.None));
        var result2 = Assert.IsType<JsonElement>(await tool.InvokeAsync(
            new AIFunctionArguments(args), CancellationToken.None));

        Assert.Equal(result1.GetProperty("tab_id").GetString(), result2.GetProperty("tab_id").GetString());

        var documentDock = GetDocumentDock(viewModel);
        var entityTabs = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(doc => string.Equals(doc.Id, entityId.ToString(), StringComparison.Ordinal))
            .ToList();
        Assert.Single(entityTabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Entity_InvalidGuid_ReturnsError()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"entity\"").RootElement.Clone();
        var idArg = JsonDocument.Parse("\"not-a-guid\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = targetArg, ["entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Entity_MissingEntityId_ReturnsError()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"entity\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = targetArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Url_OpensWebViewTab()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"url\"").RootElement.Clone();
        var urlArg = JsonDocument.Parse("\"https://example.com\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = targetArg, ["url"] = urlArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabId = resultJson.GetProperty("tab_id").GetString()!;
        Assert.False(string.IsNullOrEmpty(tabId));

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var webDoc = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(doc => string.Equals(doc.Id, tabId, StringComparison.Ordinal));
        Assert.NotNull(webDoc);
        Assert.IsType<WebViewModel>(webDoc!.TabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Url_WithTitle_SetsTitle()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"url\"").RootElement.Clone();
        var urlArg = JsonDocument.Parse("\"https://titled.example.com\"").RootElement.Clone();
        var titleArg = JsonDocument.Parse("\"My Custom Title\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["target"] = targetArg,
                ["url"] = urlArg,
                ["title"] = titleArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabId = resultJson.GetProperty("tab_id").GetString()!;

        var documentDock = GetDocumentDock(viewModel);
        var webDoc = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Single(doc => string.Equals(doc.Id, tabId, StringComparison.Ordinal));
        Assert.Equal("My Custom Title", webDoc.TabViewModel.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Url_FocusFalse_TabAddedNotFocused()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"url\"").RootElement.Clone();
        var urlArg = JsonDocument.Parse("\"https://background.example.com\"").RootElement.Clone();
        var focusArg = JsonDocument.Parse("false").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["target"] = targetArg,
                ["url"] = urlArg,
                ["focus"] = focusArg,
            }),
            CancellationToken.None);

        // The new tab must be in the dock but must NOT be the active tab.
        var resultJson = Assert.IsType<JsonElement>(result);
        var newTabId = resultJson.GetProperty("tab_id").GetString();
        Assert.NotEqual(newTabId, viewModel.ActiveTabId);

        var documentDock = GetDocumentDock(viewModel);
        var tabIds = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Select(doc => doc.Id)
            .ToList();
        Assert.Contains(newTabId, tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Shell_OpensShellTab()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var fakeSession = new FakeTerminalSession();
        var context = new WorkspaceGuiContext
        {
            MainWindowViewModel = viewModel,
            ShortcutManager = new ShortcutManager(),
            EphemeralShellSessionOpener = (_, _, _, _) => Task.FromResult<ITerminalSession>(fakeSession),
        };
        var tool = await GetToolWithContextAsync(context, "open_tab");

        var targetArg = JsonDocument.Parse("\"shell\"").RootElement.Clone();
        var commandArg = JsonDocument.Parse("\"pwsh\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = targetArg, ["command"] = commandArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabId = resultJson.GetProperty("tab_id").GetString()!;

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var shellDoc = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(doc => string.Equals(doc.Id, tabId, StringComparison.Ordinal));
        Assert.NotNull(shellDoc);
        Assert.IsType<ShellTabViewModel>(shellDoc!.TabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Shell_WithArguments_PassedToSession()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        string? capturedCommand = null;
        IReadOnlyList<string>? capturedArguments = null;
        string? capturedWorkingDirectory = null;

        var context = new WorkspaceGuiContext
        {
            MainWindowViewModel = viewModel,
            ShortcutManager = new ShortcutManager(),
            EphemeralShellSessionOpener = (command, arguments, workingDirectory, _) =>
            {
                capturedCommand = command;
                capturedArguments = arguments;
                capturedWorkingDirectory = workingDirectory;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            },
        };
        var tool = await GetToolWithContextAsync(context, "open_tab");

        var targetArg = JsonDocument.Parse("\"shell\"").RootElement.Clone();
        var commandArg = JsonDocument.Parse("\"bash\"").RootElement.Clone();
        var argsArg = JsonDocument.Parse("[\"-c\", \"ls\"]").RootElement.Clone();
        var wdArg = JsonDocument.Parse("\"/home/user\"").RootElement.Clone();
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["target"] = targetArg,
                ["command"] = commandArg,
                ["arguments"] = argsArg,
                ["working_directory"] = wdArg,
            }),
            CancellationToken.None);

        Assert.Equal("bash", capturedCommand);
        Assert.Equal(["-c", "ls"], capturedArguments);
        Assert.Equal("/home/user", capturedWorkingDirectory);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Shell_WithTitle_SetsTitle()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var context = new WorkspaceGuiContext
        {
            MainWindowViewModel = viewModel,
            ShortcutManager = new ShortcutManager(),
            EphemeralShellSessionOpener = (_, _, _, _) => Task.FromResult<ITerminalSession>(new FakeTerminalSession()),
        };
        var tool = await GetToolWithContextAsync(context, "open_tab");

        var targetArg = JsonDocument.Parse("\"shell\"").RootElement.Clone();
        var commandArg = JsonDocument.Parse("\"pwsh\"").RootElement.Clone();
        var titleArg = JsonDocument.Parse("\"My Shell Tab\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["target"] = targetArg,
                ["command"] = commandArg,
                ["title"] = titleArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        var tabId = resultJson.GetProperty("tab_id").GetString()!;

        var documentDock = GetDocumentDock(viewModel);
        var shellDoc = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Single(doc => string.Equals(doc.Id, tabId, StringComparison.Ordinal));
        Assert.Equal("My Shell Tab", shellDoc.TabViewModel.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_UnknownTarget_ReturnsError()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var targetArg = JsonDocument.Parse("\"foobar\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = targetArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_MissingTarget_ReturnsError()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "open_tab");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
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
        return await GetToolWithContextAsync(context, toolName);
    }

    private static async Task<AIFunction> GetToolWithContextAsync(WorkspaceGuiContext context, string toolName)
    {
        var provider = new WorkspaceGuiContextProvider(context);
        var tools = await GetToolsAsync(provider);
        return (AIFunction)tools.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
    }

    private static IDocumentDock? GetDocumentDock(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        return contentLayout is null ? null : FindDocumentDockIn(contentLayout);
    }

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockIn(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly MemoryStream stream = new();

        public Stream Stream => this.stream;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public Task<int> WaitForExitAsync() => Task.FromResult(0);

        public ValueTask DisposeAsync()
        {
            this.stream.Dispose();
            return ValueTask.CompletedTask;
        }
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
