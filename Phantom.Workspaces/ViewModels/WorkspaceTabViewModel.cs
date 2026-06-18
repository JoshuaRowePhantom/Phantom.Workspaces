namespace Phantom.Workspaces.ViewModels;

public abstract class WorkspaceTabViewModel : ViewModelBase
{
    private string title = string.Empty;

    public required string Id { get; init; }

    public required string Title
    {
        get => this.title;
        set => this.SetProperty(ref this.title, value);
    }

    public string DockRegion { get; init; } = "full";

    public SubscribedEntityViewModel? Entity { get; init; }
}
