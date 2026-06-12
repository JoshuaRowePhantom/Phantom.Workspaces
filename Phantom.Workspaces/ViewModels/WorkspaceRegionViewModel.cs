using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class WorkspaceRegionViewModel : ViewModelBase
{
    public WorkspaceRegionViewModel()
    {
        this.CloseTabCommand = new RelayCommand(this.OnCloseTab);
    }

    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string DockRegion { get; init; }

    public required double RelativeSize { get; init; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = new();

    public RelayCommand CloseTabCommand { get; }

    private WorkspaceTabViewModel? selectedTab;

    public WorkspaceTabViewModel? SelectedTab
    {
        get => this.selectedTab;
        set => this.SetProperty(ref this.selectedTab, value);
    }

    private void OnCloseTab(
        object? parameter)
    {
        if (parameter is not WorkspaceTabViewModel tab
            || !this.Tabs.Contains(tab))
        {
            return;
        }

        var selectedIndex = this.SelectedTab is null
            ? -1
            : this.Tabs.IndexOf(this.SelectedTab);
        var closingIndex = this.Tabs.IndexOf(tab);
        this.Tabs.Remove(tab);
        _ = DisposeTabAsync(tab);

        if (this.Tabs.Count == 0)
        {
            this.SelectedTab = null;
            return;
        }

        if (ReferenceEquals(this.SelectedTab, tab))
        {
            var nextIndex = selectedIndex;
            if (nextIndex >= this.Tabs.Count)
            {
                nextIndex = this.Tabs.Count - 1;
            }

            if (nextIndex < 0)
            {
                nextIndex = 0;
            }

            this.SelectedTab = this.Tabs[nextIndex];
            return;
        }

        if (selectedIndex > closingIndex && selectedIndex - 1 >= 0 && selectedIndex - 1 < this.Tabs.Count)
        {
            this.SelectedTab = this.Tabs[selectedIndex - 1];
        }
    }

    private static async Task DisposeTabAsync(
        WorkspaceTabViewModel tab)
    {
        switch (tab)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
