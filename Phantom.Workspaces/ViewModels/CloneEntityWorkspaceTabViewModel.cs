namespace Phantom.Workspaces.ViewModels;

public sealed class CloneEntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public CloneEntityEditorViewModel Editor { get; }

    public CloneEntityWorkspaceTabViewModel(
        SubscribedEntityViewModel sourceEntity,
        MainWindowViewModel mainWindowViewModel)
    {
        this.Id = $"clone-entity-{sourceEntity.EntityId}";
        this.Title = $"Clone: {sourceEntity.DisplayName}";
        this.DockRegion = "full";
        this.Entity = sourceEntity;
        this.TabHeader = TabHeaderViewModel.WithIcon("⧉", this.Title);
        this.Editor = new CloneEntityEditorViewModel(sourceEntity, mainWindowViewModel, this);
    }
}
