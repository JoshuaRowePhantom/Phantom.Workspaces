using System.Collections.ObjectModel;

namespace Phantom.Workspaces.ViewModels;

public sealed class WorkspacePaneViewModel : ViewModelBase
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public ObservableCollection<WorkspaceRegionViewModel> Regions { get; } = new();

    private WorkspaceRegionViewModel? selectedRegion;

    public WorkspaceRegionViewModel? SelectedRegion
    {
        get => this.selectedRegion;
        set => this.SetProperty(ref this.selectedRegion, value);
    }
}
