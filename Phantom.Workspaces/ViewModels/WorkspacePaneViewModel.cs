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
    private IRootDock? contentLayout;
    private bool anyTabIsRunning;
    private bool anyTabHasUnreadNotification;
    private WorkspaceTabViewModel? selectedTab;

    private readonly List<(WorkspaceTabViewModel tab, System.ComponentModel.PropertyChangedEventHandler tabHandler)> subscribedTabs = [];
    private readonly List<(IStatusItem tabStatus, System.ComponentModel.PropertyChangedEventHandler handler)> subscribedTabStatuses = [];

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
        this.Tabs.CollectionChanged += this.OnTabsCollectionChanged;
    }

    public string Id { get; }

    public string Title
    {
        get => this.title;
        private set => this.SetProperty(ref this.title, value);
    }

    public SubscribedEntityViewModel Entity { get; }

    public RelayCommand? CloseCommand { get; }

    /// <summary>
    /// Ordered list of open tabs in their current visual order (left to right).
    /// This is the source of truth for open tabs — used for Alt+N indexing, alt-label assignment,
    /// aggregated status, and all business-logic tab enumeration.
    /// Kept in sync with the dock model's VisibleDockables order via CollectionChanged subscription.
    /// </summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = new();

    /// <summary>
    /// The currently active/selected tab in this pane.
    /// Updated by <see cref="MainWindowViewModel"/> when the dock's active dockable changes.
    /// </summary>
    public WorkspaceTabViewModel? SelectedTab
    {
        get => this.selectedTab;
        set => this.SetProperty(ref this.selectedTab, value);
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
    /// True if any tab in this pane has a running agent session.
    /// Aggregated directly from <see cref="Tabs"/> via each tab's <see cref="WorkspaceTabViewModel.TabStatus"/>.
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

    private void OnEntityPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.DisplayName), StringComparison.Ordinal))
        {
            this.Title = this.Entity.DisplayName;
        }
    }

    private void OnTabsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.ResubscribeToTabs();
        this.RecomputeAnyTabIsRunning();
    }

    private void ResubscribeToTabs()
    {
        foreach (var (tab, handler) in this.subscribedTabs)
            tab.PropertyChanged -= handler;
        this.subscribedTabs.Clear();

        foreach (var (tabStatus, handler) in this.subscribedTabStatuses)
            tabStatus.PropertyChanged -= handler;
        this.subscribedTabStatuses.Clear();

        foreach (var tab in this.Tabs)
        {
            System.ComponentModel.PropertyChangedEventHandler tabHandler = (_, e) =>
            {
                if (string.Equals(e.PropertyName, nameof(WorkspaceTabViewModel.TabStatus), StringComparison.Ordinal))
                {
                    this.ResubscribeToTabStatuses();
                    this.RecomputeAnyTabIsRunning();
                }
            };
            tab.PropertyChanged += tabHandler;
            this.subscribedTabs.Add((tab, tabHandler));
        }

        this.ResubscribeToTabStatuses();
    }

    private void ResubscribeToTabStatuses()
    {
        foreach (var (tabStatus, handler) in this.subscribedTabStatuses)
            tabStatus.PropertyChanged -= handler;
        this.subscribedTabStatuses.Clear();

        foreach (var tab in this.Tabs)
        {
            if (tab.TabStatus is { } ts)
            {
                System.ComponentModel.PropertyChangedEventHandler statusHandler = (_, _) => this.RecomputeAnyTabIsRunning();
                ts.PropertyChanged += statusHandler;
                this.subscribedTabStatuses.Add((ts, statusHandler));
            }
        }
    }

    private void RecomputeAnyTabIsRunning()
    {
        var running = this.Tabs.Any(t => t.TabStatus?.RunningStatus == RunningStatus.Running);
        this.AnyTabIsRunning = running;
    }
}
