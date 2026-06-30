using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public sealed class WorkspacePaneViewModel : ViewModelBase
{
    private string title;
    private WorkspaceRegionViewModel? selectedRegion;
    private IRootDock? contentLayout;
    private bool anyTabIsRunning;
    private bool anyTabHasUnreadNotification;

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
        this.PaneStatus.PropertyChanged += this.OnPaneStatusPropertyChanged;
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

    /// <summary>
    /// Dock layout for this workspace's content tabs (entity tabs, agent sessions, etc.)
    /// </summary>
    public IRootDock? ContentLayout
    {
        get => this.contentLayout;
        set => this.SetProperty(ref this.contentLayout, value);
    }

    /// <summary>
    /// Aggregated status from all tabs across all regions in this pane.
    /// </summary>
    public StatusItem PaneStatus { get; } = new();

    /// <summary>
    /// True if any tab in this pane has a running agent session.
    /// Derived from <see cref="PaneStatus"/>.
    /// </summary>
    public bool AnyTabIsRunning
    {
        get => this.anyTabIsRunning;
        private set => this.SetProperty(ref this.anyTabIsRunning, value);
    }

    /// <summary>
    /// True if any tab in this pane has an unread notification.
    /// Set by <see cref="MainWindowViewModel"/> during notification aggregation.
    /// </summary>
    public bool AnyTabHasUnreadNotification
    {
        get => this.anyTabHasUnreadNotification;
        set => this.SetProperty(ref this.anyTabHasUnreadNotification, value);
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

    private void OnPaneStatusPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(IStatusItem.RunningStatus), StringComparison.Ordinal))
        {
            this.AnyTabIsRunning = this.PaneStatus.RunningStatus == RunningStatus.Running;
        }
    }

    private readonly List<(WorkspaceRegionViewModel region, NotifyCollectionChangedEventHandler handler)> subscribedRegions = [];
    private readonly List<(IStatusItem tabStatus, System.ComponentModel.PropertyChangedEventHandler handler)> subscribedTabStatuses = [];

    private void OnRegionsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.HasRegions));
        this.RaisePropertyChanged(nameof(this.HasNoRegions));
        this.ResubscribeToRegions();
        this.RecomputePaneStatus();
    }

    private void ResubscribeToRegions()
    {
        // Unsubscribe from all region tab collections and tab statuses
        foreach (var (region, handler) in this.subscribedRegions)
            region.Tabs.CollectionChanged -= handler;
        this.subscribedRegions.Clear();

        foreach (var (tabStatus, handler) in this.subscribedTabStatuses)
            tabStatus.PropertyChanged -= handler;
        this.subscribedTabStatuses.Clear();

        // Subscribe to each region's Tabs collection
        foreach (var region in this.Regions)
        {
            NotifyCollectionChangedEventHandler tabsHandler = (_, _) =>
            {
                this.ResubscribeToTabStatuses();
                this.RecomputePaneStatus();
            };
            region.Tabs.CollectionChanged += tabsHandler;
            this.subscribedRegions.Add((region, tabsHandler));
        }

        this.ResubscribeToTabStatuses();
    }

    private void ResubscribeToTabStatuses()
    {
        foreach (var (tabStatus, handler) in this.subscribedTabStatuses)
            tabStatus.PropertyChanged -= handler;
        this.subscribedTabStatuses.Clear();

        foreach (var tab in this.Regions.SelectMany(r => r.Tabs))
        {
            if (tab.TabStatus is { } ts)
            {
                System.ComponentModel.PropertyChangedEventHandler statusHandler = (_, _) => this.RecomputePaneStatus();
                ts.PropertyChanged += statusHandler;
                this.subscribedTabStatuses.Add((ts, statusHandler));
            }
        }
    }

    private void RecomputePaneStatus()
    {
        var allTabStatuses = this.Regions
            .SelectMany(r => r.Tabs)
            .Select(t => t.TabStatus)
            .Where(s => s is not null)
            .Select(s => s!);
        StatusItemAggregator.UpdateFrom(this.PaneStatus, allTabStatuses);
    }
}
