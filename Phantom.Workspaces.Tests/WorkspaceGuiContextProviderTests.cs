using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceGuiContextProviderTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceList_ReturnsAllWorkspacePanes_WithCorrectIsSelectedFlag()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "active-tab-a", Title = "Active Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "active-tab-b", Title = "Active Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // tabB is active after opening

        // Yield to ensure activation state propagates through the Dock layout manager
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);

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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "tab_list");
        var idArg = JsonDocument.Parse("\"dddddddd-dddd-4ddd-dddd-dddddddddddd\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["workspace_entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    // ── workspace_close tests ─────────────────────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceClose_ExistingPane_RemovesPaneAndReturnsClosed()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("aa110001-aa11-4aa1-aa11-aa1100000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$$"""
            {
              "entity-id": "{{{workspaceId}}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "close-existing"]],
              "display-name": { "default": "Close Existing Workspace" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var initialCount = viewModel.WorkspacePanes.Count;
        Assert.Contains(viewModel.WorkspacePanes, p => p.Id == workspaceId.ToString());

        var tool = await GetToolAsync(viewModel, "workspace_close");
        var idArg = JsonDocument.Parse($"\"{workspaceId}\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["workspace_entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("closed").GetBoolean());
        Assert.DoesNotContain(viewModel.WorkspacePanes, p => p.Id == workspaceId.ToString());
        Assert.Equal(initialCount - 1, viewModel.WorkspacePanes.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceClose_UnknownPaneId_NoOpReturnsClosed()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();
        var initialCount = viewModel.WorkspacePanes.Count;

        var tool = await GetToolAsync(viewModel, "workspace_close");
        var idArg = JsonDocument.Parse("\"ffffffff-ffff-4fff-ffff-ffffffffffff\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["workspace_entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("closed").GetBoolean());
        Assert.Equal(initialCount, viewModel.WorkspacePanes.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceClose_DefaultPlaceholderPane_NoOpReturnsClosed()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Add a pane with the default placeholder ID back to exercise the no-op branch in RemoveWorkspacePaneAsync
        using var entityDoc = JsonDocument.Parse("""
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["entity", "workspace"],
              "display-name": "No workspace selected."
            }
            """);
        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(Guid.Empty),
                ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
                Data = entityDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
        var placeholderPane = new WorkspacePaneViewModel(entity, "default-workspace");
        viewModel.WorkspacePanes.Add(placeholderPane);
        var initialCount = viewModel.WorkspacePanes.Count;

        var tool = await GetToolAsync(viewModel, "workspace_close");
        var idArg = JsonDocument.Parse("\"default-workspace\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["workspace_entity_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("closed").GetBoolean());
        Assert.Contains(viewModel.WorkspacePanes, p => p.Id == "default-workspace");
        Assert.Equal(initialCount, viewModel.WorkspacePanes.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceClose_MissingWorkspaceEntityId_ReturnsError()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "workspace_close");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    // ── tab_close tests ───────────────────────────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabClose_ExistingTab_ClosesTabAndReturnsClosedTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://close.example.com") { Id = "close-tab-existing", Title = "Close Tab" };
        await viewModel.OpenTabAsync(tab);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Contains(documentDock!.VisibleDockables!.OfType<WorkspaceDocument>(), d => d.Id == "close-tab-existing");

        var tool = await GetToolAsync(viewModel, "tab_close");
        var idArg = JsonDocument.Parse("\"close-tab-existing\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tab_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("closed").GetBoolean());
        var updatedDock = GetDocumentDock(viewModel);
        Assert.DoesNotContain(
            updatedDock?.VisibleDockables?.OfType<WorkspaceDocument>() ?? Enumerable.Empty<WorkspaceDocument>(),
            d => d.Id == "close-tab-existing");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabClose_UnknownTabId_ReturnsClosedFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "tab_close");
        var idArg = JsonDocument.Parse("\"tab-does-not-exist\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["tab_id"] = idArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.False(resultJson.GetProperty("closed").GetBoolean());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabClose_MissingTabId_ReturnsError()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "tab_close");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    // ── entity_invoke_shortcut tests ──────────────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_EntityFound_ValidShortcut_ReturnsHandled()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("bb220001-bb22-4bb2-bb22-bb2200000001");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "task"],
              "names": [["tests", "shortcut", "invoke-1"]],
              "display-name": { "default": "Shortcut Invoke Test Entity" }
            }
            """);

        var tool = await GetToolAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"Open\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("handled", out _));
        Assert.False(resultJson.TryGetProperty("error", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_EntityNotFound_ReturnsHandledFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse("\"cccccccc-cccc-4ccc-cccc-cccccccccccc\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"Open\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.False(resultJson.GetProperty("handled").GetBoolean());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_UnknownShortcut_ReturnsError()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse("\"dddddddd-dddd-4ddd-dddd-dddddddddddd\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"NotAShortcut\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out var errorElement));
        Assert.Contains("Review", errorElement.GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_MissingEntityId_ReturnsError()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tool = await GetToolAsync(viewModel, "entity_invoke_shortcut");
        var shortcutArg = JsonDocument.Parse("\"Open\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["shortcut"] = shortcutArg }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.TryGetProperty("error", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_Review_OnGitWorktreeEntity_ReturnsHandledTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("ff440001-ff44-4ff4-ff44-ff4400000001");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["tests", "worktrees", "review-shortcut-1"]],
              "display-name": { "default": "Review Shortcut Test Worktree" }
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"Review\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("handled").GetBoolean());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_Review_OnNonGitWorktreeEntity_ReturnsHandledFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("ff440002-ff44-4ff4-ff44-ff4400000002");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "task"],
              "names": [["tests", "tasks", "review-shortcut-2"]],
              "display-name": { "default": "Review Shortcut Test Task" }
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"Review\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.False(resultJson.GetProperty("handled").GetBoolean());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_CopyEntityId_IsRecognizedShortcut()
    {
        // Issue #1215: the CopyEntityId shortcut must be wired into the MCP tool (enum +
        // ResolveShortcut + registered handler). In the headless test harness there is no
        // MainWindow, so the default clipboard accessor yields null and the handler declines
        // (handled:false with a reason) — but the shortcut is still RECOGNIZED, i.e. the tool
        // does not return an "Unknown shortcut" error.
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("ff440003-ff44-4ff4-ff44-ff4400000003");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "task"],
              "names": [["tests", "tasks", "copy-id-shortcut-1"]],
              "display-name": { "default": "Copy Id Shortcut Test Task" }
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"CopyEntityId\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        // Recognized: the response carries a "handled" property rather than an "Unknown shortcut" error.
        Assert.True(resultJson.TryGetProperty("handled", out _));
        Assert.False(resultJson.TryGetProperty("error", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_ReviewOnGitWorktree_OpensReviewTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("aa550001-aa55-4aa5-aa55-aa5500000001");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["tests", "worktrees", "review-tab-1"]],
              "display-name": { "default": "Review Tab Test Worktree" }
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"Review\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("handled").GetBoolean());

        var documentDock = GetDocumentDock(viewModel);
        var reviewTab = documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<GitWorktreeReviewWorkspaceTabViewModel>()
            .FirstOrDefault();
        Assert.NotNull(reviewTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_VsCodeWebOnGitWorktree_OpensWebViewTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Seed a vscode-tunnel for the local profile
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var localProfiles = await entityBroker.GetEntitiesAsync([localProfileId], TestContext.Current.CancellationToken);
        var profile = localProfiles.Single();

        if (profile.Data is not JsonElement profileData)
        {
            Assert.Fail("Profile data is null");
            return;
        }

        var namesArray = profileData.GetProperty("names");
        var primaryName = namesArray[0];
        var nameParts = primaryName.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] == "username")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }
        Assert.NotNull(userSegment);

        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new JsonArray("entity", "vscode-tunnel"),
            ["names"] = new JsonArray(new JsonArray(userSegment, "vscode-tunnel")),
            ["display-name"] = new JsonObject { ["default"] = "invoke-shortcut-tunnel" },
            ["tunnel-name"] = "invoke-shortcut-tunnel",
            ["tunnel-url"] = "https://vscode.dev/tunnel/invoke-shortcut-tunnel",
            ["active"] = true,
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel for VsCodeWeb." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        // Seed the git-worktree entity (names don't resolve to a profile, so handler uses local profile)
        var entityId = new EntityId("aa550002-aa55-4aa5-aa55-aa5500000002");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["tests", "worktrees", "vscode-web-tab-1"]],
              "display-name": { "default": "VsCodeWeb Tab Test Worktree" },
              "path": "/test/vscode-web-repo"
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"VsCodeWeb\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("handled").GetBoolean());

        var documentDock = GetDocumentDock(viewModel);
        var webViewTab = documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<WebViewModel>()
            .FirstOrDefault(t => t.Title?.StartsWith("VS Code Web", StringComparison.Ordinal) == true);
        Assert.NotNull(webViewTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_StartAgentSessionOnGitWorktree_OpensStartAgentSessionTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("aa550003-aa55-4aa5-aa55-aa5500000003");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["tests", "worktrees", "agent-session-1"]],
              "display-name": { "default": "Agent Session Test Worktree" },
              "path": "/test/repo"
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"StartAgentSession\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("handled").GetBoolean());

        var documentDock = GetDocumentDock(viewModel);
        var agentSessionTab = documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<StartAgentSessionOnProfileViewModel>()
            .FirstOrDefault();
        Assert.NotNull(agentSessionTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_StartShellOnGitWorktree_OpensShellTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Replace production handler with a fake to avoid spawning a real PTY
        viewModel.ShortcutManager.ReplaceShortcutHandlerForTesting<StartShellFromEntityShortcutHandler>(
            new StartShellFromEntityShortcutHandler(
                (_, _, _) => Task.FromResult<ITerminalSession>(new FakeTerminalSession())));

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("aa550004-aa55-4aa5-aa55-aa5500000004");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["tests", "worktrees", "shell-tab-1"]],
              "display-name": { "default": "Shell Tab Test Worktree" },
              "path": "/test/repo"
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"StartShell\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("handled").GetBoolean());

        var documentDock = GetDocumentDock(viewModel);
        var shellTab = documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<ShellTabViewModel>()
            .FirstOrDefault();
        Assert.NotNull(shellTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_VsCodeOnGitWorktree_RemainsWorking()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Replace production handler with a fake to avoid running the VS Code CLI
        viewModel.ShortcutManager.ReplaceShortcutHandlerForTesting<OpenInVsCodeShortcutHandler>(
            new OpenInVsCodeShortcutHandler(
                cliLocator: () => "code",
                processRunner: (_, _) => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, string.Empty)),
                urlLauncher: null));

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("aa550005-aa55-4aa5-aa55-aa5500000005");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["tests", "worktrees", "vscode-1"]],
              "display-name": { "default": "VsCode Test Worktree" },
              "path": "/test/repo"
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        var shortcutArg = JsonDocument.Parse("\"VsCode\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.True(resultJson.GetProperty("handled").GetBoolean());
        Assert.False(resultJson.TryGetProperty("reason", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityInvokeShortcut_NoHandlerApplies_ReturnsReason()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("aa550006-aa55-4aa5-aa55-aa5500000006");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": ["entity", "task"],
              "names": [["tests", "tasks", "no-handler-1"]],
              "display-name": { "default": "No Handler Task" }
            }
            """);

        var tool = await GetToolWithViewModelShortcutManagerAsync(viewModel, "entity_invoke_shortcut");
        var idArg = JsonDocument.Parse($"\"{entityId}\"").RootElement.Clone();
        // Review shortcut applies only to git-worktree entities; this entity is a plain "task"
        var shortcutArg = JsonDocument.Parse("\"Review\"").RootElement.Clone();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity_id"] = idArg,
                ["shortcut"] = shortcutArg,
            }),
            CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.False(resultJson.GetProperty("handled").GetBoolean());
        Assert.True(resultJson.TryGetProperty("reason", out var reasonEl));
        var reason = reasonEl.GetString()!;
        Assert.NotEmpty(reason);
        Assert.Contains("Review", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── ProvideAIContextAsync instructions tests ──────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ProvideAIContextAsync_InstructionsEntityPresent_LoadsInstructions()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var instructionsId = new EntityId("ee330001-ee33-4ee3-ee33-ee3300000001");
        const string expectedText = "Use workspace_list to enumerate open panes.";
        await UpsertEntityAndLoadAsync(entityBroker, instructionsId, $$"""
            {
              "entity-id": "{{instructionsId}}",
              "entity-types": ["entity", "note"],
              "names": [["documentation", "entity-workspace-gui-agent-tool-instructions"]],
              "display-name": { "default": "Workspace GUI Tool Instructions" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": {
                    "text": "{{expectedText}}"
                  }
                }
              }
            }
            """);

        var context = await GetContextAsync(viewModel);

        Assert.NotNull(context.Instructions);
        Assert.Contains(expectedText, context.Instructions, StringComparison.Ordinal);
    }

    // ── open_tab tests ────────────────────────────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Entity_OpensEntityTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        Assert.Equal("My Custom Title", webDoc.TabViewModel!.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_Url_FocusFalse_TabAddedNotFocused()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        Assert.Equal("My Shell Tab", shellDoc.TabViewModel!.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabTool_UnknownTarget_ReturnsError()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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

    private static async Task<AIFunction> GetToolWithViewModelShortcutManagerAsync(MainWindowViewModel viewModel, string toolName)
    {
        var context = new WorkspaceGuiContext
        {
            MainWindowViewModel = viewModel,
            ShortcutManager = viewModel.ShortcutManager,
        };
        return await GetToolWithContextAsync(context, toolName);
    }

    private static async Task<AIFunction> GetToolWithContextAsync(WorkspaceGuiContext context, string toolName)
    {
        var provider = new WorkspaceGuiContextProvider(context);
        var tools = await GetToolsAsync(provider);
        return (AIFunction)tools.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
    }

    private static async Task<AIContext> GetContextAsync(MainWindowViewModel viewModel)
    {
        var context = new WorkspaceGuiContext
        {
            MainWindowViewModel = viewModel,
            ShortcutManager = new ShortcutManager(),
        };
        var provider = new WorkspaceGuiContextProvider(context);
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetContextAsync(provider, agent, session, CancellationToken.None);
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
