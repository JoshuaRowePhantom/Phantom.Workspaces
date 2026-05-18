using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class WorkspacePaneViewModel : ViewModelBase
{
    private string title;
    private WorkspaceRegionViewModel? selectedRegion;

    public WorkspacePaneViewModel(
        SubscribedEntityViewModel entity,
        string? id = null)
    {
        this.Entity = entity;
        this.title = entity.DisplayName;
        this.Id = id ?? entity.EntityId.ToString();
        this.Entity.PropertyChanged += this.OnEntityPropertyChanged;
    }

    public string Id { get; }

    public string Title
    {
        get => this.title;
        private set => this.SetProperty(ref this.title, value);
    }

    public SubscribedEntityViewModel Entity { get; }

    public ObservableCollection<WorkspaceRegionViewModel> Regions { get; } = [];

    public WorkspaceRegionViewModel? SelectedRegion
    {
        get => this.selectedRegion;
        set => this.SetProperty(ref this.selectedRegion, value);
    }

    public void SetRegions(
        IEnumerable<WorkspaceRegionViewModel> regions)
    {
        this.Regions.Clear();
        foreach (var region in regions)
        {
            this.Regions.Add(region);
        }

        this.SelectedRegion = this.Regions.FirstOrDefault();
    }

    private void OnEntityPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.DisplayName), StringComparison.Ordinal))
        {
            this.Title = this.Entity.DisplayName;
        }
    }
}
