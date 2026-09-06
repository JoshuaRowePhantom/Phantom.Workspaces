using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for issue #1461: the workspace "New Agent" shortcut
/// (<see cref="NewAgentOnWorkspaceShortcutHandler"/>) opens a
/// <see cref="NewAgentSelectionViewModel"/> tab hosting the shared manifest-parameter component in
/// the workspace's own pane.
/// </summary>
public sealed class NewAgentOnWorkspaceShortcutHandlerTests
{
    private const string WorkspaceEntityId = "e1461001-0000-4000-8000-000000000001";

    private static readonly string WorkspaceEntityJson =
        $$"""
        {
          "entity-id": "{{WorkspaceEntityId}}",
          "entity-types": ["entity", "workspace"],
          "display-name": { "default": "New Agent WS" },
          "regions": []
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentOnWorkspaceShortcut_WhenInvoked_OpensManifestSelectionTab()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await using (viewModel)
        {
            await viewModel.InitializeAsync();

            var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
            var workspaceId = new EntityId(WorkspaceEntityId);
            var workspaceEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
                broker, workspaceId, WorkspaceEntityJson);

            await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
            var pane = Assert.Single(
                viewModel.WorkspacePanes,
                p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
            await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);
            viewModel.SelectedWorkspacePane = pane;

            var handler = new NewAgentOnWorkspaceShortcutHandler(
                new AgentSessionShortcutContext(),
                new OpenAgentSessionShortcutHandler(
                    new AgentSessionShortcutContext(),
                    MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
                    MainWindowIntegrationTests.CreateTestRunningAgentChatTable()));

            Assert.True(await handler.ShouldApplyTo(viewModel, Shortcut.NewAgentOnWorkspace, workspaceEntity));
            Assert.True(await handler.Handle(viewModel, Shortcut.NewAgentOnWorkspace, workspaceEntity));

            Assert.Contains(pane.Tabs, tab => tab is NewAgentSelectionViewModel);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NewAgentOnWorkspaceShortcut_WhenNotWorkspaceEntity_DoesNotApply()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await using (viewModel)
        {
            await viewModel.InitializeAsync();

            var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
            var taskId = new EntityId(Guid.NewGuid());
            var taskEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
                broker,
                taskId,
                $$"""
                {
                  "entity-id": "{{taskId.Value}}",
                  "entity-types": ["entity", "task"],
                  "display-name": { "default": "Not a workspace" }
                }
                """);

            var handler = new NewAgentOnWorkspaceShortcutHandler(
                new AgentSessionShortcutContext(),
                new OpenAgentSessionShortcutHandler(
                    new AgentSessionShortcutContext(),
                    MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
                    MainWindowIntegrationTests.CreateTestRunningAgentChatTable()));

            Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.NewAgentOnWorkspace, taskEntity));
        }
    }
}
