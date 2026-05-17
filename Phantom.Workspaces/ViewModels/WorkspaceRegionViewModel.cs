using System.Collections.ObjectModel;

namespace Phantom.Workspaces.ViewModels;

public sealed class WorkspaceRegionViewModel : ViewModelBase
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string DockRegion { get; init; }

    public required double RelativeSize { get; init; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = new();

    private WorkspaceTabViewModel? selectedTab;

    public WorkspaceTabViewModel? SelectedTab
    {
        get => this.selectedTab;
        set => this.SetProperty(ref this.selectedTab, value);
    }
}
