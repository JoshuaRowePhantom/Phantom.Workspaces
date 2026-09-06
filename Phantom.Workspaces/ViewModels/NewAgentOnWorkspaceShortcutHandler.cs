using System.Linq;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Handles the <see cref="Shortcut.NewAgentOnWorkspace"/> shortcut on a <c>workspace</c> entity
/// (issue #1461). Opens a <see cref="NewAgentSelectionViewModel"/> tab in that workspace's pane so
/// the user can pick a manifest, edit its parameters, and launch an agent session that is related to
/// and saved with the workspace.
/// </summary>
public sealed class NewAgentOnWorkspaceShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public NewAgentOnWorkspaceShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
    }

    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.NewAgentOnWorkspace
            && entityViewModel.IsEntityType("workspace"));
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var workspacePane = ResolveWorkspacePane(mainWindowViewModel, entityViewModel);
        if (workspacePane is null)
        {
            return false;
        }

        var selectionTab = new NewAgentSelectionViewModel(
            mainWindowViewModel,
            this.agentSessionShortcutContext,
            this.openAgentSessionShortcutHandler,
            workspacePane)
        {
            Id = $"new-agent-selection-{entityViewModel.EntityId}",
            Title = $"New Agent on {entityViewModel.DisplayName}",
            DockRegion = "full",
            Entity = entityViewModel,
            TabHeader = TabHeaderViewModel.WithIcon("✨", $"New Agent on {entityViewModel.DisplayName}"),
        };

        await mainWindowViewModel.OpenTabAsync(selectionTab, focus: true, workspacePaneId: workspacePane.Id);
        return true;
    }

    private static WorkspacePaneViewModel? ResolveWorkspacePane(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        var byEntity = mainWindowViewModel.WorkspacePanes
            .FirstOrDefault(pane => pane.Entity.EntityId == entityViewModel.EntityId);
        if (byEntity is not null)
        {
            return byEntity;
        }

        var selected = mainWindowViewModel.SelectedWorkspacePane;
        return selected is { CanSaveWorkspace: true } ? selected : null;
    }
}
