namespace Phantom.Workspaces.ViewModels;

public sealed class CloneEntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public CloneEntityEditorViewModel? Editor { get; private set; }

    public static CloneEntityWorkspaceTabViewModel Create(
        SubscribedEntityViewModel sourceEntity,
        MainWindowViewModel mainWindowViewModel)
    {
        var title = $"Clone: {sourceEntity.DisplayName}";
        var tab = new CloneEntityWorkspaceTabViewModel
        {
            Id = $"clone-entity-{sourceEntity.EntityId}",
            Title = title,
            DockRegion = "full",
            Entity = sourceEntity,
            TabHeader = TabHeaderViewModel.WithIcon("⧉", title),
        };
        tab.Editor = new CloneEntityEditorViewModel(sourceEntity, mainWindowViewModel, tab);
        return tab;
    }
}
