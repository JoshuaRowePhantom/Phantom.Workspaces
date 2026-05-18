namespace Phantom.Workspaces.ViewModels;

public abstract class WorkspaceTabViewModel : ViewModelBase
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string DockRegion { get; init; } = "full";

    public SubscribedEntityViewModel? Entity { get; init; }
}
