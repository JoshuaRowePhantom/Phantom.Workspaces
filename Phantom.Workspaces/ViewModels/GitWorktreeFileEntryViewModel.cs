namespace Phantom.Workspaces.ViewModels;

public sealed class GitWorktreeFileEntryViewModel : ViewModelBase
{
    private bool isSelected;

    public required string RelativePath { get; init; }
    public required int LinesAdded { get; init; }
    public required int LinesRemoved { get; init; }

    public bool IsSelected
    {
        get => this.isSelected;
        set => this.SetProperty(ref this.isSelected, value);
    }
}
