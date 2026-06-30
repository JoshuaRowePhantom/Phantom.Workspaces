namespace Phantom.Workspaces.ViewModels;

public sealed class NavigationStackRowViewModel : ViewModelBase
{
    private bool isSelected;

    public required string TabTitle { get; init; }
    public string? WorkspaceName { get; init; }
    public bool IsRunning { get; init; }
    public bool IsInteresting { get; init; }

    public bool IsSelected
    {
        get => this.isSelected;
        set => this.SetProperty(ref this.isSelected, value);
    }
}
