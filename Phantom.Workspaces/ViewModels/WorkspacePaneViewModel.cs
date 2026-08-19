using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.Navigation;

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
    private readonly WorkspacePaneEnvironment? environment;

    /// <summary>
    /// #1341: this pane's own <c>tabId → WorkspaceDocument</c> registry. Previously a single
    /// window-scoped dictionary on <see cref="WorkspaceDockFactory"/> that outlived panes and left
    /// stale entries (the mechanism (A) of #1340). Owning it here means closing the pane discards
    /// the registry wholesale, so a reopened pane starts empty and the collision guard cannot
    /// false-positive against a prior owner's entry.
    /// </summary>
    private readonly Dictionary<string, WorkspaceDocument> documentsByTabId = new(StringComparer.Ordinal);

    private readonly List<(WorkspaceTabViewModel tab, System.ComponentModel.PropertyChangedEventHandler tabHandler)> subscribedTabs = [];
    private readonly List<(IStatusItem tabStatus, System.ComponentModel.PropertyChangedEventHandler handler)> subscribedTabStatuses = [];
    private readonly TaskCompletionSource populatedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkspacePaneViewModel(
        SubscribedEntityViewModel entity,
        string? id = null,
        RelayCommand? closeCommand = null,
        Func<WorkspacePaneViewModel, Task>? saveAsync = null,
        bool isReadOnly = false,
        WorkspacePaneEnvironment? environment = null)
    {
        this.Entity = entity;
        this.saveAsync = saveAsync;
        this.environment = environment;
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

    /// <summary>
    /// True when a save handler is wired (i.e. this pane represents a real workspace that can be
    /// persisted, not a placeholder / no-workspace-selected pane). Bound by the top-right save
    /// button's IsVisible so a permanently-disabled affordance is not shown on placeholder panes.
    /// </summary>
    public bool CanSaveWorkspace => this.saveAsync is not null;

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

    // ── #1341: per-pane tabId → WorkspaceDocument registry ────────────────────

    /// <summary>Returns the <see cref="WorkspaceDocument"/> registered for the given tab id in
    /// THIS pane, or null if none. Replaces the former window-scoped
    /// <c>WorkspaceDockFactory.GetDocumentForTab</c>.</summary>
    public WorkspaceDocument? GetDocumentForTab(string tabId)
        => this.documentsByTabId.TryGetValue(tabId, out var doc) ? doc : null;

    /// <summary>True when THIS pane owns a materialized document for the given document tab id.
    /// Used by the navigate-to-tab "search all panes" fallback (#1341).</summary>
    public bool OwnsDocumentTab(string documentTabId)
        => this.documentsByTabId.ContainsKey(documentTabId);

    /// <summary>Registers a document for the given tab id in this pane's registry. Also updates the
    /// shared factory's <c>DockableLocator</c> so Dock's restore-by-id can resolve it.</summary>
    public void RegisterDocument(string tabId, WorkspaceDocument document)
    {
        this.documentsByTabId[tabId] = document;
        var locator = this.environment?.DockFactory.DockableLocator;
        if (locator is not null)
        {
            locator[tabId] = () => this.documentsByTabId.GetValueOrDefault(tabId);
        }
    }

    /// <summary>Removes the document registration for the given tab id from this pane's registry.</summary>
    public void UnregisterDocument(string tabId)
    {
        this.documentsByTabId.Remove(tabId);
        this.environment?.DockFactory.DockableLocator?.Remove(tabId);
    }

    // ── #1341: navigate-to-document Phase-2 (per-pane) ────────────────────────

    /// <summary>
    /// Phase-2 of navigate-to-tab-by-id (#1341). Resolves <paramref name="documentTabId"/> against
    /// THIS pane's registry and activates + focuses it within this pane's own
    /// <see cref="ContentLayout"/>. If the tab is not yet materialized but is present in
    /// <see cref="Tabs"/> (or <paramref name="deferIfAbsent"/> is set because the pane was just
    /// opened and is still hydrating), installs a deferred activation hook and returns true.
    /// Returns false when the document tab id is unknown to this pane.
    /// </summary>
    public Task<bool> NavigateToDocumentTabAsync(string documentTabId, bool deferIfAbsent = false)
    {
        if (this.ContentLayout is null)
        {
            return Task.FromResult(false);
        }

        var doc = this.GetDocumentForTab(documentTabId);
        if (doc is not null)
        {
            var documentDock = FindDocumentDock(this.ContentLayout);
            this.environment?.DockFactory.SetActiveDockable(doc);
            if (documentDock is not null)
            {
                this.environment?.DockFactory.SetFocusedDockable(documentDock, doc);
            }
            return Task.FromResult(true);
        }

        var inTabs = this.Tabs.Any(t => string.Equals(t.Id, documentTabId, StringComparison.Ordinal));
        if (inTabs || deferIfAbsent)
        {
            this.InstallDeferredActivation(documentTabId);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// #1341: deferred activation (formerly <c>MainWindowViewModel.ActivateTabWhenLoaded</c>).
    /// Subscribes to this pane's <see cref="Tabs"/> and activates the document tab once it
    /// materializes. Fires exactly once.
    /// </summary>
    private void InstallDeferredActivation(string documentTabId)
    {
        void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var tab = this.Tabs.FirstOrDefault(t => string.Equals(t.Id, documentTabId, StringComparison.Ordinal));
            if (tab is null) return;

            this.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            this.ActivateMaterializedDocument(documentTabId);
        }

        this.Tabs.CollectionChanged += OnTabsCollectionChanged;

        // Race-condition guard: check again after subscribing.
        var existing = this.Tabs.FirstOrDefault(t => string.Equals(t.Id, documentTabId, StringComparison.Ordinal));
        if (existing is not null)
        {
            this.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            this.ActivateMaterializedDocument(documentTabId);
        }
    }

    private void ActivateMaterializedDocument(string documentTabId)
    {
        var doc = this.GetDocumentForTab(documentTabId);
        if (doc is null || this.ContentLayout is null) return;

        var documentDock = FindDocumentDock(this.ContentLayout);
        this.environment?.SelectPane(this);
        this.environment?.DockFactory.SetActiveDockable(doc);
        if (documentDock is not null)
        {
            this.environment?.DockFactory.SetFocusedDockable(documentDock, doc);
        }
    }

    // ── #1341: close-active-tab / cycle (per-pane) ────────────────────────────

    /// <summary>#1341: closes the active tab in this pane (formerly
    /// <c>MainWindowViewModel.OnCloseActiveTab</c>). Routes through the dock factory so Dock's
    /// default collapse chain removes an emptied split region + splitter, then runs window-global
    /// MRU navigation via the environment.</summary>
    public void CloseActiveTab()
    {
        if (this.ContentLayout is null)
        {
            return;
        }

        var documentDock = FindFocusedDocumentDock(this.ContentLayout)
            ?? FindDocumentDock(this.ContentLayout);
        if (documentDock?.ActiveDockable is not WorkspaceDocument activeDoc)
        {
            return;
        }

        global::Dock.Model.Core.IFactory? factory = documentDock.Factory ?? this.environment?.DockFactory;
        factory?.CloseDockable(activeDoc);

        this.environment?.RunMruNavigationAfterActiveClose();
    }

    /// <summary>#1341: cycles the active tab in this pane in visible order (formerly
    /// <c>MainWindowViewModel.OnCycleTab</c>).</summary>
    public void CycleTab(int delta)
    {
        if (this.ContentLayout is null || this.Tabs.Count < 2)
        {
            return;
        }

        var documentDock = FindDocumentDock(this.ContentLayout);
        if (documentDock is null)
        {
            return;
        }

        var docs = documentDock.VisibleDockables?.OfType<WorkspaceDocument>().ToList()
            ?? new List<WorkspaceDocument>();
        if (docs.Count < 2)
        {
            return;
        }

        var activeTabId = (documentDock.ActiveDockable as WorkspaceDocument)?.Id;
        var currentIndex = activeTabId is not null
            ? docs.FindIndex(d => string.Equals(d.Id, activeTabId, StringComparison.Ordinal))
            : 0;
        if (currentIndex < 0) currentIndex = 0;

        var nextIndex = ((currentIndex + delta) % docs.Count + docs.Count) % docs.Count;
        var nextDoc = docs[nextIndex];

        this.environment?.DockFactory.SetActiveDockable(nextDoc);
        this.environment?.DockFactory.SetFocusedDockable(documentDock, nextDoc);
        this.environment?.NotificationService.MarkRead(nextDoc.Id);
    }

    /// <summary>#1341: the per-pane portion of <c>MainWindowViewModel.OnDockableTabClosed</c> —
    /// removes the closed child tab from this pane and runs MRU navigation if it was active.</summary>
    public void HandleChildTabClosed(WorkspaceTabViewModel tabVm)
    {
        var wasActive = false;
        if (this.ContentLayout is not null)
        {
            var documentDock = FindDocumentDock(this.ContentLayout);
            wasActive = documentDock?.ActiveDockable is WorkspaceDocument activeDoc
                && string.Equals(activeDoc.Id, tabVm.Id, StringComparison.Ordinal);
        }

        this.Tabs.Remove(tabVm);

        if (wasActive)
        {
            this.environment?.RunMruNavigationAfterActiveClose();
        }
    }

    // ── #1341: populate / restore (per-pane) ──────────────────────────────────

    /// <summary>#1341: loads this pane's tabs (formerly
    /// <c>MainWindowViewModel.PopulateWorkspacePaneTabsAsync</c>). Prefers a saved dock-layout
    /// restore and falls back to the tab-declaration list, then to a default entity tab.</summary>
    public async Task PopulateTabsAsync(SubscribedEntityViewModel workspaceEntity, JsonElement workspaceData)
    {
        if (this.environment is null)
        {
            return;
        }

        if (workspaceData.TryGetProperty("dock-layout", out var dockLayoutElement)
            && dockLayoutElement.ValueKind == JsonValueKind.Object)
        {
            var dockLayoutJson = dockLayoutElement.GetRawText();
            if (await this.TryRestoreFromDockLayoutAsync(workspaceEntity, dockLayoutJson))
            {
                return;
            }
        }

        var tabDeclarations = MainWindowViewModel.CollectTabDeclarations(workspaceData);

        string? activeTabId = null;
        if (workspaceData.TryGetProperty("active-tab-id", out var activeTabIdElement)
            && activeTabIdElement.ValueKind == JsonValueKind.String)
        {
            activeTabId = activeTabIdElement.GetString();
        }
        else if (workspaceData.TryGetProperty("focused-tab-id", out var focusedTabIdElement)
            && focusedTabIdElement.ValueKind == JsonValueKind.String)
        {
            activeTabId = focusedTabIdElement.GetString();
        }

        var tabAdded = false;
        if (tabDeclarations.Count > 0)
        {
            var tabResults = await Task.WhenAll(
                tabDeclarations.Select(tabDecl => this.environment.TabFactory.TryFetchWorkspaceTabAsync(tabDecl)));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var workspaceClosed = false;
                foreach (var workspaceTab in tabResults)
                {
                    if (workspaceTab is null) continue;

                    if (workspaceClosed || !this.environment.IsPaneLive(this))
                    {
                        workspaceClosed = true;
                        _ = DisposeTabAsync(workspaceTab);
                        continue;
                    }

                    this.Tabs.Add(workspaceTab);
                    tabAdded = true;
                }

                if (tabAdded && !workspaceClosed && !string.IsNullOrEmpty(activeTabId))
                {
                    var focusedDoc = this.GetDocumentForTab(activeTabId);
                    if (focusedDoc is not null)
                    {
                        this.environment.DockFactory.SetActiveDockable(focusedDoc);
                    }
                }
            });
        }

        if (!tabAdded)
        {
            var defaultTab = this.environment.TabFactory.CreateDefaultWorkspaceTab(workspaceEntity);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!this.environment.IsPaneLive(this))
                {
                    _ = DisposeTabAsync(defaultTab);
                    return;
                }

                this.Tabs.Add(defaultTab);
            });
        }
    }

    /// <summary>#1341: restores this pane's tabs from saved dock-layout JSON (formerly
    /// <c>MainWindowViewModel.TryRestoreFromDockLayoutAsync</c>).
    /// #1340: <c>success</c> tracks whether each restored tab actually produced a
    /// <see cref="WorkspaceDocument"/> in a content dock (registry hit) — not merely that
    /// <see cref="Tabs"/>.Add completed — so a generator that fails to materialize a document
    /// falls through to the legacy default-tab fallback instead of silently rendering an empty pane.</summary>
    public async Task<bool> TryRestoreFromDockLayoutAsync(
        SubscribedEntityViewModel workspaceEntity,
        string dockLayoutJson)
    {
        if (this.environment is null)
        {
            return false;
        }

        IRootDock? layout;
        try
        {
            layout = DockLayoutCanonicalizer.Deserialize(dockLayoutJson);
        }
        catch
        {
            return false;
        }

        if (layout is null) return false;

        DockLayoutCanonicalizer.Canonicalize(layout, liveTabIds: null);
        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(layout);

        var stubs = MainWindowViewModel.EnumerateAllDocuments(layout)
            .Where(d => d.Descriptor is not null)
            .ToList();

        if (stubs.Count == 0) return false;

        var tabVmTasks = stubs.Select(stub =>
            this.environment.TabFactory.CreateTabViewModelFromDescriptorAsync(workspaceEntity, stub.Descriptor!, stub.Id));
        var tabResults = await Task.WhenAll(tabVmTasks);

        bool success = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!this.environment.IsPaneLive(this)) return;

            this.environment.DockFactory.ContextLocator ??= new Dictionary<string, Func<object?>>();
            for (int i = 0; i < stubs.Count; i++)
            {
                var tabVm = tabResults[i];
                if (tabVm is null) continue;
                var stubId = stubs[i].Id;
                this.environment.DockFactory.ContextLocator[stubId] = () => tabVm;
            }

            // #1333: detach the pre-restore default content dock so its generator does not steal
            // the tab registrations from the restored region docks.
            if (this.ContentLayout is not null
                && FindDocumentDock(this.ContentLayout) is WorkspaceContentDock previousPrimaryDock)
            {
                previousPrimaryDock.ItemsSource = null;
            }

            this.ContentLayout = layout;
            this.environment.DockFactory.InitLayout(layout);
            this.environment.DockFactory.DockState.Restore(layout);

            var contentDocks = MainWindowViewModel.EnumerateContentDocks(layout).ToList();
            var primaryDock = contentDocks.FirstOrDefault();
            foreach (var dock in contentDocks)
            {
                this.environment.DockFactory.WireContentDock(
                    dock, this, ownsTabs: ReferenceEquals(dock, primaryDock));
            }

            bool workspaceClosed = false;
            for (int i = 0; i < stubs.Count; i++)
            {
                var tabVm = tabResults[i];
                if (tabVm is null) continue;

                if (workspaceClosed || !this.environment.IsPaneLive(this))
                {
                    workspaceClosed = true;
                    _ = DisposeTabAsync(tabVm);
                    continue;
                }

                this.Tabs.Add(tabVm);

                // #1340: only treat the restore as successful if the tab actually materialized a
                // WorkspaceDocument (registry hit), not merely that Tabs.Add completed. Defence in
                // depth: any future regression in the generator (collision guard, ItemsSource
                // wiring, etc.) then falls through to the default-tab fallback instead of silently
                // rendering "no documents open".
                if (this.GetDocumentForTab(tabVm.Id) is not null)
                {
                    success = true;
                }
            }
        });

        return success;
    }

    // ── #1341: persisted snapshot (per-pane serialization body) ──────────────

    /// <summary>#1341: builds the entity changes that persist this pane's tabs + dock layout
    /// (the serialization body of the former <c>MainWindowViewModel.WriteBackWorkspaceTabs</c>).
    /// The actual <c>entityBroker.UpdateAsync</c> write stays on <see cref="MainWindowViewModel"/>.
    /// Returns null when the pane entity has no serializable data.</summary>
    internal List<EntityChange>? BuildPersistedTabsSnapshot()
    {
        var entityData = this.Entity.Data;
        if (entityData is not JsonElement dataElement || dataElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tabDescriptors = new JsonArray();
        foreach (var tab in this.Tabs)
        {
            var descriptor = BuildTabDescriptor(tab);
            if (descriptor is not null)
            {
                tabDescriptors.Add(descriptor);
            }
        }

        JsonNode? dockLayout = null;
        if (this.ContentLayout is not null)
        {
            foreach (var openDoc in MainWindowViewModel.EnumerateAllDocuments(this.ContentLayout))
            {
                if (openDoc.TabViewModel is { } liveTab)
                {
                    var refreshed = WorkspaceDocument.BuildDescriptor(liveTab);
                    if (refreshed is not null)
                    {
                        openDoc.Descriptor = refreshed;
                    }
                }
            }

            this.environment?.DockFactory.DockState.Save(this.ContentLayout);

            var liveTabIds = this.Tabs
                .Select(t => t.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToArray();
            var layoutJson = DockLayoutCanonicalizer.SerializeCanonical(this.ContentLayout, liveTabIds);

            if (!string.IsNullOrWhiteSpace(layoutJson))
            {
                dockLayout = JsonNode.Parse(layoutJson);
            }
        }

        var entityNode = JsonNode.Parse(dataElement.GetRawText())?.AsObject();
        if (entityNode is null) return null;

        entityNode["tabs"] = tabDescriptors;

        var documentDock = this.ContentLayout is not null
            ? FindDocumentDock(this.ContentLayout)
            : null;
        var activeTabId = (documentDock?.ActiveDockable as WorkspaceDocument)?.Id;
        if (activeTabId is not null)
        {
            entityNode["active-tab-id"] = activeTabId;
        }
        else
        {
            entityNode.Remove("active-tab-id");
        }
        if (dockLayout is not null)
        {
            entityNode["dock-layout"] = dockLayout;
        }
        else
        {
            entityNode.Remove("dock-layout");
        }

        entityNode.Remove("focused-tab-id");

        var updatedJson = entityNode.ToJsonString();
        using var doc = JsonDocument.Parse(updatedJson);
        var updatedData = doc.RootElement.Clone();

        var changes = new List<EntityChange>
        {
            new()
            {
                EntityId = this.Entity.EntityId,
                ConcurrencyTag = this.Entity.ConcurrencyTag,
                Data = updatedData,
                EntityChangeMode = EntityChangeMode.Replace,
            },
        };
        AppendWorkspaceTabRelationshipChanges(this, changes);

        return changes;
    }

    private static void AppendWorkspaceTabRelationshipChanges(
        WorkspacePaneViewModel workspacePane,
        ICollection<EntityChange> changes)
    {
        var liveEntityIds = workspacePane.Tabs
            .Select(static tab => tab.Entity?.EntityId)
            .OfType<EntityId>()
            .Distinct()
            .ToHashSet();

        var existingByTarget = new Dictionary<EntityId, EntitySnapshot>();
        foreach (var relationship in workspacePane.Entity.Relationships)
        {
            if (relationship.Data is not JsonElement data
                || !EntityPresentation.IsEntityType(relationship, "related")
                || !data.TryGetProperty("note", out var note)
                || note.ValueKind != JsonValueKind.String
                || !string.Equals(note.GetString(), "Workspace save records the live entity tabs associated with this workspace.", StringComparison.Ordinal)
                || !RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(data, out var participantIds)
                || !participantIds.Contains(workspacePane.Entity.EntityId))
            {
                continue;
            }

            var targetId = participantIds.FirstOrDefault(id => id != workspacePane.Entity.EntityId);
            if (targetId != default)
            {
                existingByTarget[targetId] = relationship;
            }
        }

        foreach (var removed in existingByTarget.Where(pair => !liveEntityIds.Contains(pair.Key)))
        {
            changes.Add(new EntityChange
            {
                EntityId = removed.Value.EntityId,
                ConcurrencyTag = removed.Value.ConcurrencyTag,
                Data = null,
                EntityChangeMode = EntityChangeMode.Replace,
            });
        }

        foreach (var added in liveEntityIds.Where(id => !existingByTarget.ContainsKey(id)))
        {
            var relationshipId = Guid.NewGuid();
            var relationshipData = new JsonObject
            {
                ["entity-id"] = relationshipId.ToString(),
                ["entity-types"] = new JsonArray("entity", "relationship", "related"),
                ["participants"] = new JsonObject
                {
                    ["entities"] = new JsonArray(workspacePane.Entity.EntityId.Value.ToString(), added.Value.ToString()),
                },
                ["note"] = "Workspace save records the live entity tabs associated with this workspace.",
            };
            using var doc = JsonDocument.Parse(relationshipData.ToJsonString());
            changes.Add(new EntityChange
            {
                EntityId = new EntityId(relationshipId),
                Data = doc.RootElement.Clone(),
                EntityChangeMode = EntityChangeMode.Replace,
            });
        }
    }

    /// <summary>Builds a workspace-tab-descriptor <see cref="JsonObject"/> for write-back.
    /// Returns null for tab types that cannot be serialized.</summary>
    private static JsonObject? BuildTabDescriptor(WorkspaceTabViewModel tab)
    {
        JsonObject? content = null;

        if (tab.Entity is { } entity)
        {
            content = new JsonObject
            {
                ["target-entity-name"] = entity.EntityId.Value.ToString(),
            };
        }
        else if (tab is WebViewModel webVm && !string.IsNullOrWhiteSpace(webVm.AddressBarUrl))
        {
            content = new JsonObject
            {
                ["url"] = webVm.AddressBarUrl,
            };
        }

        if (content is null) return null;

        return new JsonObject
        {
            ["tab-id"] = tab.Id,
            ["title"] = tab.Title,
            ["kind"] = tab.DockRegion,
            ["content"] = content,
        };
    }

    // ── #1341: dock-tree helpers (per-pane copies) ────────────────────────────

    private static IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDock(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static IDocumentDock? FindFocusedDocumentDock(IDockable dockable)
    {
        IDockable? focused = dockable;
        while (focused is IDock currentDock && currentDock.FocusedDockable is { } childFocus
               && !ReferenceEquals(childFocus, currentDock))
        {
            focused = childFocus;
        }

        for (IDockable? cursor = focused; cursor is not null; cursor = cursor.Owner)
        {
            if (cursor is IDocumentDock documentDock)
            {
                return documentDock;
            }
        }

        return null;
    }

    private static async Task DisposeTabAsync(WorkspaceTabViewModel tab)
    {
        await tab.DisposeAsync();
    }

    /// <summary>
    /// Recursively disposes every tab in <see cref="Tabs"/> so that the pane-close path
    /// releases per-tab resources (notably the <c>RunningAgentChatLease</c> owned by
    /// <see cref="AgentSessionWorkspaceTabViewModel"/>). See #1198.
    /// #1341: also discards this pane's document registry wholesale, so a reopened pane starts
    /// empty and no stale prior-owner entry can trip the collision guard (structural fix for
    /// #1340 mechanism (A)).
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        var snapshot = this.Tabs.ToArray();
        this.Tabs.Clear();

        foreach (var tabId in this.documentsByTabId.Keys.ToArray())
        {
            this.UnregisterDocument(tabId);
        }
        this.documentsByTabId.Clear();

        foreach (var tab in snapshot)
        {
            try
            {
                await tab.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Continue disposing siblings; a single tab disposal failure must not
                // strand the remaining tabs (or their leases).
            }
        }
        await base.DisposeAsync().ConfigureAwait(false);
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
