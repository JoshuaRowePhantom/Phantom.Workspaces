using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    private bool isSaving;
    private WorkspaceTabViewModel? selectedTab;
    private readonly Func<WorkspacePaneViewModel, Task>? saveAsync;

    private readonly List<(WorkspaceTabViewModel tab, System.ComponentModel.PropertyChangedEventHandler tabHandler)> subscribedTabs = [];
    private readonly List<(IStatusItem tabStatus, System.ComponentModel.PropertyChangedEventHandler handler)> subscribedTabStatuses = [];
    private readonly TaskCompletionSource populatedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkspacePaneViewModel(
        SubscribedEntityViewModel entity,
        string? id = null,
        RelayCommand? closeCommand = null,
        Func<WorkspacePaneViewModel, Task>? saveAsync = null,
        bool isReadOnly = false)
    {
        this.Entity = entity;
        this.saveAsync = saveAsync;
        this.IsReadOnly = isReadOnly;
        this.title = entity.DisplayName;
        this.Id = id ?? entity.EntityId.ToString();
        this.CloseCommand = closeCommand;
        this.SaveCommand = new AsyncRelayCommand(
            async _ => await this.SaveAsync(),
            _ => !this.IsReadOnly && !this.isSaving && this.saveAsync is not null);
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

    public AsyncRelayCommand SaveCommand { get; }

    public bool IsReadOnly { get; }

    public bool IsSaving
    {
        get => this.isSaving;
        private set
        {
            if (this.SetProperty(ref this.isSaving, value))
            {
                this.SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Task that completes when <see cref="MainWindowViewModel.PopulateWorkspacePaneTabsAsync"/> finishes.
    /// Exceptions raised during populate are propagated to this task.
    /// Tests should await this instead of relying on implicit Tabs.CollectionChanged events.
    /// </summary>
    public Task Populated => this.populatedTcs.Task;

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
        // #1135: When agent-session tabs are added (via OpenTabAsync or workspace restore),
        // stamp the tab's WorkspacePaneId with THIS pane's Id so TabDescriptor.WorkspaceId
        // and status-button navigation reflect the pane the tab actually lives in — not the
        // pane that happened to be SelectedWorkspacePane when the tab was constructed.
        if (e.NewItems is not null)
        {
            foreach (var newItem in e.NewItems)
            {
                if (newItem is AgentSessionWorkspaceTabViewModel agentTab)
                {
                    agentTab.WorkspacePaneId = this.Id;
                }
            }
        }

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

    /// <summary>
    /// Signals that <see cref="MainWindowViewModel.PopulateWorkspacePaneTabsAsync"/> has completed.
    /// Called by <see cref="MainWindowViewModel"/> after populate finishes (successfully or with error).
    /// </summary>
    internal void SignalPopulated(Exception? error = null)
    {
        if (error is not null)
        {
            this.populatedTcs.TrySetException(error);
        }
        else
        {
            this.populatedTcs.TrySetResult();
        }
    }

    private async Task SaveAsync()
    {
        if (this.saveAsync is null || this.IsReadOnly || this.isSaving)
        {
            return;
        }

        this.IsSaving = true;
        try
        {
            await this.saveAsync(this);
        }
        finally
        {
            this.IsSaving = false;
        }
    }
}
