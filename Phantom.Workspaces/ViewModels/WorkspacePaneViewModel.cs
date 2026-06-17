using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class WorkspacePaneViewModel : ViewModelBase
{
    private string title;
    private WorkspaceRegionViewModel? selectedRegion;

    public WorkspacePaneViewModel(
        SubscribedEntityViewModel entity,
        string? id = null,
        RelayCommand? closeCommand = null)
    {
        this.Entity = entity;
        this.title = entity.DisplayName;
        this.Id = id ?? entity.EntityId.ToString();
        this.CloseCommand = closeCommand;
        this.Entity.PropertyChanged += this.OnEntityPropertyChanged;
        this.Regions.CollectionChanged += this.OnRegionsCollectionChanged;
    }

    public string Id { get; }

    public string Title
    {
        get => this.title;
        private set => this.SetProperty(ref this.title, value);
    }

    public SubscribedEntityViewModel Entity { get; }

    public RelayCommand? CloseCommand { get; }

    public ObservableCollection<WorkspaceRegionViewModel> Regions { get; } = [];

    public bool HasRegions => this.Regions.Count > 0;

    public bool HasNoRegions => !this.HasRegions;

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
        this.RaisePropertyChanged(nameof(this.HasRegions));
        this.RaisePropertyChanged(nameof(this.HasNoRegions));
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

    private void OnRegionsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.HasRegions));
        this.RaisePropertyChanged(nameof(this.HasNoRegions));
    }
}
