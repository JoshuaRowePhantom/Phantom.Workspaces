using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class EditAgentManifestShortcutHandler : ShortcutHandler
{
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Edit
            && entityViewModel.IsEntityType("agent-manifest");
    }

    public override Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var tabId = $"edit-manifest-{entityViewModel.EntityId}";
        var editorVm = new AgentManifestEditorViewModel(entityViewModel, mainWindowViewModel)
        {
            Id = tabId,
            Title = $"Edit: {entityViewModel.DisplayName}",
            DockRegion = "full",
            Entity = entityViewModel,
            TabHeader = new IconTabHeaderViewModel { Icon = "✏️", Title = $"Edit: {entityViewModel.DisplayName}" },
        };
        _ = mainWindowViewModel.OpenTabAsync(editorVm);
        return Task.FromResult(true);
    }
}
