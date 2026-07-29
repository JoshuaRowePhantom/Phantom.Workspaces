using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IProfileAppearanceController, IWorkspaceTabService, IActiveTabProvider, IAsyncDisposable
{
    private const string DefaultWorkspaceId = "default-workspace";
    private const string LoadingWorkspaceIdPrefix = "loading-workspace:";
    private static readonly ViewDefinitionViewModel EmptyView = new()
    {
        Id = "empty",
        Title = "No views",
        Description = string.Empty,
        IconGlyph = "◻",
    };
    private readonly Task<EntityBroker> entityBrokerTask;
    private readonly WorkspacesConfiguration? configuration;
    private WorkspacesWebHost? webHost;
    private Services.WorkspacesTransportComposition? transportComposition;
    private readonly Trust.DeferredTrustedExecutorSelector trustedExecutorSelector;
    private Services.DevTunnel.IDevTunnelHostService? devTunnelHostService;
    private Task? devTunnelHostStartTask;
    private EntityBroker? entityBroker;
    private InterestCatalog? interestCatalog;
    private EntityTypeCatalog? entityTypeCatalog;
    private EntityTypeViewCatalog? entityTypeViewCatalog;
    private FieldEditorFactory? fieldEditorFactory;
    private SubscribedEntityViewModel? mainNavigationView;
    private readonly ProfileStore profileStore;
    private readonly DispatcherTimer refreshTimer;
    private ViewPopulationViewModel currentPopulation = new();
    private readonly ShortcutManager shortcutManager = new();
    private EntityClickShortcutHandler? entityClickShortcutHandler;
    private OpenAgentSessionShortcutHandler? openAgentSessionShortcutHandler;
    private ViewDefinitionViewModel selectedTopLevelView = EmptyView;
    private WorkspacePaneViewModel selectedWorkspacePane;
    private string stickyParentContextText = string.Empty;
    private Profile currentProfile = Profile.Default;
    private string selectedThemeName = ProfileThemeSettings.Dark.Name;
    private bool suppressThemeSelectionChange;
    private bool showHiddenItems;
    private bool isLeftPaneCollapsed;
    private static readonly GridLength LeftPaneExpandedWidth = new(1, GridUnitType.Star);
    private static readonly GridLength LeftPaneCollapsedWidth = new(0, GridUnitType.Pixel);
    private readonly WorkspaceDockFactory dockFactory;
    private IRootDock? layout;
    private ScheduledTools.ScheduledToolHost? scheduledToolHost;
    private ScheduledTools.ScheduledToolPauseStateService? scheduledToolPauseStateService;
    private ScheduledTools.ScheduledToolRunner? scheduledToolRunner;
    private ScheduledToolsPauseIndicatorViewModel? scheduledToolsPause;
    private ScheduledToolsRunningViewModel? scheduledToolsRunning;
    private RunningAgentBrainViewModel? runningAgentBrain;
    private IRunningAgentChatTable? runningAgentChats;
    private UsageTrackerViewModel? usageTracker;
    private Services.UsageMetricsService? usageMetricsService;
    private readonly Microsoft.Extensions.Logging.ILoggerFactory loggerFactory;
    private readonly Services.Logging.ILogDirectoryProvider? logDirectoryProvider;
    private readonly NotificationService notificationService;
    private NotificationsViewModel? notificationsViewModel;
    private readonly NavigationHistoryService navigationHistoryService = new();
    private bool navigatingViaHistory;
    private NavigationStackPopupViewModel? navStackPopup;
    private readonly Dictionary<string, bool> expandedEntityIds = new(StringComparer.Ordinal);
    private readonly List<RunningAgentChatLease> autoResumeLeases = [];

    public MainWindowViewModel(
        RepositorySource repositorySource,
        WorkspacesConfiguration? configuration = null,
        ProfileStore? profileStore = null,
        ApplicationServices? applicationServices = null)
    {
        var services = applicationServices ?? CreateDefaultApplicationServices();
        this.loggerFactory = services.LoggerFactory
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        this.logDirectoryProvider = services.LogDirectoryProvider;
        this.RepositorySource = repositorySource;
        this.configuration = configuration;
        this.entityBrokerTask = EntityBroker.CreateInitializedAsync(
            repositorySource,
            userComputerProfileOverride: configuration?.UserComputerProfileOverride);
        this.profileStore = profileStore ?? ProfileStore.ForCurrentUser();

        this.TopLevelViews = new ObservableCollection<ViewDefinitionViewModel>();
        this.WorkspacePanes = new ObservableCollection<WorkspacePaneViewModel>();

        this.selectedWorkspacePane = CreatePlaceholderWorkspacePane(DefaultWorkspaceId, "No workspace selected.");
        this.WorkspacePanes.Add(this.selectedWorkspacePane);
        
        this.dockFactory = new WorkspaceDockFactory(this);
        
        this.ActivateShortcutCommand = new RelayCommand(async _ => await this.OnActivateShortcutAsync(_), this.CanActivateShortcut);
        this.SetDebuggingCommand = new RelayCommand(async parameter => await this.SetDebuggingAsync(ReadDebuggingParameter(parameter)));
        this.CloseWorkspaceCommand = new RelayCommand(this.OnCloseWorkspace, this.CanCloseWorkspace);
        this.CloseActiveTabCommand = new RelayCommand(_ => this.OnCloseActiveTab());
        this.CycleTabForwardCommand = new RelayCommand(_ => this.OnCycleTab(+1));
        this.CycleTabBackwardCommand = new RelayCommand(_ => this.OnCycleTab(-1));
        this.NavigateBackCommand = new RelayCommand(_ => this.OnNavigateBack());
        this.NavigateForwardCommand = new RelayCommand(_ => this.OnNavigateForward());
        this.DuplicateBrowserTabCommand = new RelayCommand(async _ => await this.DuplicateBrowserTabAsync());
        var agentSessionShortcutContext = new AgentSessionShortcutContext(
            userComputerProfileOverride: configuration?.UserComputerProfileOverride,
            persistenceStoreCache: services.AgentPersistenceStoreCache);
        var trustedExecutorSelector = new Trust.DeferredTrustedExecutorSelector();
        this.trustedExecutorSelector = trustedExecutorSelector;
        this.openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, trustedExecutorSelector, services.RunningAgentChats);
        this.shortcutManager.AddShortcutHandler(new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(this.openAgentSessionShortcutHandler);
        this.shortcutManager.AddShortcutHandler(new StartAgentSessionFromEntityShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(new StartAgentSessionOnProfileShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(new StartShellFromEntityShortcutHandler(trustedExecutorSelector));
        this.shortcutManager.AddShortcutHandler(new StartShellOnProfileShortcutHandler(trustedExecutorSelector));
        this.shortcutManager.AddShortcutHandler(new OpenExternalEntityShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new OpenShellEntityShortcutHandler(trustedExecutorSelector));
        this.shortcutManager.AddShortcutHandler(new OpenEntityShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new OpenAssociatedWorkspaceShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new DeleteEntityShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new EditAgentManifestShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new CloneEntityShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new ReviewWorktreeShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new OpenInVsCodeShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new OpenInVsCodeWebShortcutHandler());

        // The click handler opens configured entity types on a plain card click. It is intentionally
        // NOT registered with the shortcut manager, so it never produces a shortcut button; the entity
        // card click wiring invokes ActivateEntityClickCommand directly.
        this.entityClickShortcutHandler = new EntityClickShortcutHandler(["workspace"], this.shortcutManager);
        this.ActivateEntityClickCommand = new RelayCommand(async parameter => await this.OnActivateEntityClickAsync(parameter));

        this.refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        this.refreshTimer.Tick += this.OnRefreshTick;
        this.notificationService = new NotificationService(this);
        this.notificationsViewModel = new NotificationsViewModel(
            this.notificationService,
            tabId => this.NavigateToNotificationTab(tabId));
        this.NavigateNextNotificationCommand = new RelayCommand(_ => this.OnNavigateNotification(+1));
        this.NavigatePreviousNotificationCommand = new RelayCommand(_ => this.OnNavigateNotification(-1));
        this.notificationService.NotificationsChanged += this.OnNotificationsChanged;
        this.dockFactory.ActiveDockableChanged += this.OnActiveDockableChanged;

        var runningAgentChats = services.RunningAgentChats;
        this.runningAgentChats = runningAgentChats;
        this.runningAgentBrain = new RunningAgentBrainViewModel(
            runningAgentChats,
            this.GetAllAgentTabs,
            this.ActivateTabById,
            sessionKey => _ = this.OpenAgentForSessionAsync(sessionKey),
            action => Dispatcher.UIThread.Post(action));
    }

    private static ApplicationServices CreateDefaultApplicationServices()
    {
        var agentPersistenceStoreCache = new AgentPersistenceStoreCache();
        var agentPersistenceStore = AgentPersistenceStoreFactory.CreateInMemory();
        var agentChatFactory = new AgentChatFactory(agentPersistenceStore, new AgentServices(), TaskScheduler.Current);
        return new ApplicationServices(
            new RunningAgentChatTable(agentChatFactory),
            agentPersistenceStoreCache);
    }

    public RepositorySource RepositorySource { get; }

    /// <summary>The transport composition built during initialization (null before init / without configuration).</summary>
    internal Services.WorkspacesTransportComposition? TransportComposition => this.transportComposition;

    /// <summary>The transport-backed executor the production selector uses for non-local targets.</summary>
    internal Llm.Trust.ITrustedExecutor? ProductionRemoteExecutor => this.trustedExecutorSelector.RemoteExecutor;

    public ObservableCollection<ViewDefinitionViewModel> TopLevelViews { get; }

    public ObservableCollection<WorkspacePaneViewModel> WorkspacePanes { get; }

    public RelayCommand ActivateShortcutCommand { get; }

    public RelayCommand ActivateEntityClickCommand { get; }

    public RelayCommand SetDebuggingCommand { get; }

    public RelayCommand CloseWorkspaceCommand { get; }

    public RelayCommand CloseActiveTabCommand { get; }

    public RelayCommand CycleTabForwardCommand { get; }

    public RelayCommand CycleTabBackwardCommand { get; }

    public RelayCommand NavigateNextNotificationCommand { get; }
    public RelayCommand NavigatePreviousNotificationCommand { get; }
    public RelayCommand NavigateBackCommand { get; }
    public RelayCommand NavigateForwardCommand { get; }
    public RelayCommand DuplicateBrowserTabCommand { get; }

    public NotificationsViewModel? NotificationsViewModel
    {
        get => this.notificationsViewModel;
        private set => this.SetProperty(ref this.notificationsViewModel, value);
    }

    public NavigationStackPopupViewModel NavStackPopup =>
        this.navStackPopup ??= new NavigationStackPopupViewModel(
            this.navigationHistoryService,
            tabId => this.GetTabInfo(tabId));

    /// <summary>
    /// Navigate directly to the history entry at <paramref name="historyIndex"/> without
    /// pushing a new entry onto the navigation stack.
    /// </summary>
    public void NavigateToHistoryEntry(int historyIndex)
    {
        if (!this.navigationHistoryService.GoToIndex(historyIndex, out var entry) || entry is null)
        {
            return;
        }

        this.navigatingViaHistory = true;
        try
        {
            this.ActivateTabById(entry.TabId, entry.WorkspacePaneId);
        }
        finally
        {
            this.navigatingViaHistory = false;
        }
    }

    private NavigationTabInfo? GetTabInfo(string tabId)
    {
        foreach (var pane in this.WorkspacePanes)
        {
            var tab = pane.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
            if (tab is null) continue;

            var doc = this.dockFactory.GetDocumentForTab(tabId);
            if (doc is null) continue;

            var statusIndicator = doc.EffectiveTabHeader.Items
                .OfType<StatusTabHeaderItemViewModel>()
                .FirstOrDefault();
            return new NavigationTabInfo(
                doc.Title,
                pane.Title,
                statusIndicator?.Status.RunningStatus == RunningStatus.Running,
                doc.HasUnreadNotification);
        }

        return null;
    }

    // IActiveTabProvider implementation
    public string? ActiveTabId
    {
        get
        {
            var layout = this.selectedWorkspacePane?.ContentLayout;
            if (layout is null) return null;
            var documentDock = this.FindDocumentDock(layout);
            return documentDock?.ActiveDockable?.Id;
        }
    }

    public AgentViewModel? ActiveAgentViewModel
    {
        get
        {
            var layout = this.selectedWorkspacePane?.ContentLayout;
            if (layout is null) return null;
            var documentDock = this.FindDocumentDock(layout);
            if (documentDock?.ActiveDockable is not WorkspaceDocument { TabViewModel: AgentSessionWorkspaceTabViewModel agentTab })
                return null;
            return agentTab.Agent;
        }
    }

    public INotificationService NotificationService=> this.notificationService;

    public ConnectionStatusViewModel? ConnectionStatus{ get; private set; }

    public IRootDock? Layout
    {
        get => this.layout;
        private set => this.SetProperty(ref this.layout, value);
    }

    public Profile CurrentProfile => this.currentProfile;

    public IReadOnlyList<string> ThemeNames => ProfileThemeSettings.ThemeNames;

    public string SelectedThemeName
    {
        get => this.selectedThemeName;
        set
        {
            var normalizedThemeName = ProfileThemeSettings.ForName(value).Name;
            if (!this.SetProperty(ref this.selectedThemeName, normalizedThemeName))
            {
                return;
            }

            if (this.suppressThemeSelectionChange)
            {
                return;
            }

            _ = this.SetThemeAsync(normalizedThemeName).ContinueWith(
                static t => Dispatcher.UIThread.Post(() =>
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(
                        t.Exception!.InnerException ?? t.Exception!)),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    public bool IsDebuggingEnabled => this.CurrentProfile.Debugging;

    public bool IsDebuggingDisabled => !this.IsDebuggingEnabled;

    /// <summary>
    /// Whether entities marked with the <c>not-interesting</c> interest are shown. When false (the
    /// default), such entities are excluded from the view; toggling re-applies the selected view.
    /// </summary>
    public bool ShowHiddenItems
    {
        get => this.showHiddenItems;
        set
        {
            if (this.SetProperty(ref this.showHiddenItems, value))
            {
                _ = this.ApplySelectedViewAsync();
            }
        }
    }

    public ViewDefinitionViewModel SelectedTopLevelView
    {
        get => this.selectedTopLevelView;
        set
        {
            var nextSelection = value ?? EmptyView;
            if (!this.SetProperty(ref this.selectedTopLevelView, nextSelection))
            {
                return;
            }

            // Issue #1120: navigating to a different top-level view auto-expands the left pane
            // so a freshly-navigated view is never hidden behind a still-collapsed collapser.
            this.IsLeftPaneCollapsed = false;

            _ = this.ApplySelectedViewAsync();
        }
    }

    /// <summary>
    /// Issue #1120: whether the MainWindow left navigation column is collapsed. Toggled via the
    /// shared <c>pane-collapser</c> ToggleButton on the inner (right) edge of the left column;
    /// auto-reset to false on top-level navigation.
    /// </summary>
    public bool IsLeftPaneCollapsed
    {
        get => this.isLeftPaneCollapsed;
        set
        {
            if (this.SetProperty(ref this.isLeftPaneCollapsed, value))
            {
                this.RaisePropertyChanged(nameof(this.LeftPaneColumnWidth));
            }
        }
    }

    /// <summary>
    /// Issue #1120: the MainWindow left column's <see cref="GridLength"/>. Collapses to 0px when
    /// <see cref="IsLeftPaneCollapsed"/> is true and restores to <c>*</c> (proportional) otherwise.
    /// </summary>
    public GridLength LeftPaneColumnWidth =>
        this.isLeftPaneCollapsed ? LeftPaneCollapsedWidth : LeftPaneExpandedWidth;

    public WorkspacePaneViewModel SelectedWorkspacePane
    {
        get => this.selectedWorkspacePane;
        set
        {
            if (this.SetProperty(ref this.selectedWorkspacePane, value))
            {
                this.RaisePropertyChanged(nameof(this.ActiveAgentViewModel));

            }
        }
    }

    public string RepositoryStatusText => this.RepositorySource switch
    {
        WebRepositorySource web => $"Web DAL source: {web.Endpoint}",
        LocalGitRepositorySource git => $"Local git source: {git.Path}",
        MongoDbRepositorySource mongo => $"MongoDb DAL source: {mongo.ContainerName}/{mongo.RootCollectionName}",
        _ => "In-memory repository source.",
    };

    public ViewPopulationViewModel CurrentViewPopulation
    {
        get => this.currentPopulation;
        private set
        {
            if (!this.SetProperty(ref this.currentPopulation, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.HasStickyParentContext));
        }
    }

    public bool HasStickyParentContext => this.CurrentProfile.DebugOnlyIsVisible
        && !string.IsNullOrWhiteSpace(this.StickyParentContextText);

    public string StickyParentContextText
    {
        get => this.stickyParentContextText;
        private set
        {
            if (!this.SetProperty(ref this.stickyParentContextText, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.HasStickyParentContext));
        }
    }

    /// <summary>
    /// Gets the initialized broker for view-layer data access.
    /// </summary>
    /// <remarks>
    /// View code should not access the repository/DAL directly. Always use broker subscriptions so
    /// view content remains live-updating as entities change.
    /// </remarks>
    internal EntityBroker EntityBroker => this.entityBroker
        ?? throw new InvalidOperationException("The view model has not been initialized.");

    internal ShortcutManager ShortcutManager => this.shortcutManager;

    /// <summary>
    /// Reflects the persisted scheduled-tools pause state on the clock / scheduled-tools button, and
    /// toggles it. Null until <see cref="InitializeAsync"/> has composed the scheduled-tools runtime.
    /// </summary>
    public ScheduledToolsPauseIndicatorViewModel? ScheduledToolsPause
    {
        get => this.scheduledToolsPause;
        private set => this.SetProperty(ref this.scheduledToolsPause, value);
    }

    /// <summary>The running and historical tool executions for the main-window indicators.</summary>
    public ScheduledToolsRunningViewModel? ScheduledToolsRunning
    {
        get => this.scheduledToolsRunning;
        private set => this.SetProperty(ref this.scheduledToolsRunning, value);
    }

    /// <summary>
    /// The brain-button popup view model for running agent sessions.
    /// </summary>
    internal RunningAgentBrainViewModel? RunningAgentBrain
    {
        get => this.runningAgentBrain;
        private set => this.SetProperty(ref this.runningAgentBrain, value);
    }

    /// <summary>
    /// The usage-tracker toolbar button and popup view model.
    /// Null until <see cref="InitializeAsync"/> has initialized the usage tracker.
    /// </summary>
    internal UsageTrackerViewModel? UsageTracker
    {
        get => this.usageTracker;
        private set => this.SetProperty(ref this.usageTracker, value);
    }

    /// <summary>
    /// Returns an <see cref="AgentTabInfo"/> for every open agent-session tab that is in the
    /// <see cref="AgentTabState.Ready"/> state, across all workspace panes.
    /// </summary>
    internal IEnumerable<AgentTabInfo> GetAllAgentTabs()
    {
        foreach (var pane in this.WorkspacePanes)
        {
            foreach (var tab in pane.Tabs)
            {
                if (tab is AgentSessionWorkspaceTabViewModel agentTab
                    && agentTab.State == AgentTabState.Ready)
                {
                    yield return new AgentTabInfo(pane.Id, pane.Title, agentTab);
                }
            }
        }
    }

    /// <summary>
    /// Called from <see cref="OpenAgentSessionShortcutHandler"/> after an agent tab transitions to
    /// <see cref="AgentTabState.Ready"/> or <see cref="AgentTabState.Failed"/>.
    /// Triggers a refresh of the running-agent brain popup so newly-ready tabs appear immediately.
    /// </summary>
    internal void NotifyAgentTabStateChanged() => this.runningAgentBrain?.Refresh();

    /// <summary>
    /// Creates the scheduled tasks view model (scheduled tool-relationships plus the tool-execution
    /// results tree), or returns null if the workspace has not finished initializing.
    /// </summary>
    internal ScheduledTasksViewModel? TryCreateScheduledTasksViewModel()
        => this.entityBroker is { } broker
            ? new ScheduledTasksViewModel(
                broker,
                this.scheduledToolPauseStateService,
                this.HostProfileEntityId,
                this.scheduledToolHost,
                action => Dispatcher.UIThread.Post(action))
            : null;

    private EntityId HostProfileEntityId =>
        this.entityBroker?.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId
        ?? default;

    public async Task InitializeAsync()
    {
        this.entityBroker = await this.entityBrokerTask;

        // The broker refreshes subscriptions off the UI thread (UpdateAsync runs on the thread pool);
        // marshal its UI-bound collection/snapshot mutations onto the Avalonia dispatcher.
        this.entityBroker.UiMarshal = static action =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Invoke(action);
            }
        };

        this.entityBroker.Changed += this.OnEntityBrokerChanged;
        this.interestCatalog = await InterestCatalog.CreateAsync(this.entityBroker);
        this.interestCatalog.Changed += this.OnInterestCatalogChanged;
        this.entityTypeCatalog = await EntityTypeCatalog.CreateAsync(this.entityBroker);
        this.entityTypeCatalog.Changed += this.OnEntityTypeCatalogChanged;
        this.entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(this.entityBroker);
        this.fieldEditorFactory = new FieldEditorFactory(
            this.entityBroker,
            this.entityTypeViewCatalog,
            entityReferenceSearch: new EntityReferenceSearch(this.entityBroker),
            openEntity: entityId => _ = this.OpenEntityByIdAsync(entityId));
        this.mainNavigationView = await this.LoadNavigationSubscriptionAsync();
        this.InitializeTopLevelViews();
        await this.ApplySelectedViewAsync();
        await this.InitializeProfileAsync();
        this.InitializeDockLayout(); // Initialize workspace-level dock
        if (this.configuration?.SkipStartupWorkspace != true)
        {
            await this.OpenStartupWorkspaceAsync();
        }
        this.refreshTimer.Start();
        await this.InitializeWebHostAsync();
        await this.InitializeScheduledToolsAsync();
        await this.InitializeAutoResumeAsync();
        await this.InitializeUsageTrackerAsync();
    }

    /// <summary>
    /// Composes the scheduled-tools runtime for the current <c>user-computer-profile</c> host: builds
    /// the registry of built-in scheduled tools, the host, the persisted pause-state service (which
    /// also surfaces the pause indicator), and starts the periodic runner. The runner stops on
    /// <see cref="DisposeAsync"/>. (See <c>docs/design/scheduled-tools.md</c>.)
    /// </summary>
    private async Task InitializeScheduledToolsAsync()
    {
        if (this.entityBroker is not { } broker)
        {
            return;
        }

        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var hostEntityId = this.HostProfileEntityId;
        var hostNameComponents = await this.ResolveHostNameComponentsAsync(hostEntityId);

        var registry = new ScheduledTools.ScheduledToolRegistry(
        [
            new Tools.VectorIndexerTool(),
            new Tools.GitWorkspaceDiscoveryTool(),
            new Tools.GitWorkspaceUpdateTool(),
            new Tools.CopilotSessionDiscoveryTool(),
            new Tools.VsCodeTunnelDiscoveryTool(),
            new Tools.RunVsCodeTunnelTool(),
            new Tools.GitHub.GitHubWorkItemDiscoveryTool(),
            new Tools.AzureDevOps.AzureDevOpsWorkItemDiscoveryTool(),
        ]);
        this.scheduledToolHost = new ScheduledTools.ScheduledToolHost(dataAccessLayer, registry);
        this.scheduledToolPauseStateService = new ScheduledTools.ScheduledToolPauseStateService(
            dataAccessLayer,
            this.scheduledToolHost);
        await this.scheduledToolPauseStateService.RefreshAsync(hostEntityId);
        this.ScheduledToolsPause = new ScheduledToolsPauseIndicatorViewModel(
            this.scheduledToolPauseStateService,
            hostEntityId,
            action => Dispatcher.UIThread.Post(action));

        this.ScheduledToolsRunning = new ScheduledToolsRunningViewModel(
            this.scheduledToolHost,
            dataAccessLayer,
            action => Dispatcher.UIThread.Post(action));
        _ = this.ScheduledToolsRunning.RefreshHistoryAsync();

        this.scheduledToolRunner = ScheduledTools.ScheduledToolRunner.Create(
            this.scheduledToolHost,
            hostEntityId,
            hostNameComponents,
            pollInterval: TimeSpan.FromMinutes(1));
        this.scheduledToolRunner.Start();
    }

    private async Task InitializeAutoResumeAsync()
    {
        if (this.entityBroker is not { } broker || this.openAgentSessionShortcutHandler is null)
        {
            return;
        }

        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var sessions = await AutoResumeService.FindMatchingSessionsAsync(
            dataAccessLayer, Llm.Trust.TrustProfile.LocalClientInstance);

        if (sessions.Count == 0)
        {
            return;
        }

        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

        foreach (var session in sessions)
        {
            var entities = await broker.GetEntitiesAsync([session.EntityId]);
            var entity = entities.FirstOrDefault(e => e.EntityId == session.EntityId);
            if (entity is null)
            {
                continue;
            }

            var lease = await this.openAgentSessionShortcutHandler.TryStartAutoResumeAsync(
                this, entity, session.ResumePrompt, foregroundScheduler);

            if (lease is not null)
            {
                this.autoResumeLeases.Add(lease);
            }
        }
    }

    private async Task InitializeUsageTrackerAsync()
    {
        if (this.entityBroker is not { } broker)
        {
            return;
        }

        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var metrics = new Models.UsageMetrics(foregroundScheduler);
        var vm = new UsageTrackerViewModel(
            metrics,
            this.loggerFactory.CreateLogger<UsageTrackerViewModel>());
        this.UsageTracker = vm;

        var providers = new List<Services.UsageProviders.IUsageProvider>
        {
            new Services.UsageProviders.GitHubCopilotUsageProvider(
                new HttpClient(),
                this.loggerFactory.CreateLogger<Services.UsageProviders.GitHubCopilotUsageProvider>(),
                TimeProvider.System),
            new Services.UsageProviders.GitHubActionsUsageProvider(
                new HttpClient(),
                this.loggerFactory.CreateLogger<Services.UsageProviders.GitHubActionsUsageProvider>(),
                TimeProvider.System),
        };
        this.usageMetricsService = new Services.UsageMetricsService(
            dataAccessLayer,
            metrics,
            providers,
            TimeProvider.System,
            this.loggerFactory.CreateLogger<Services.UsageMetricsService>());
        await this.usageMetricsService.StartAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<string>> ResolveHostNameComponentsAsync(EntityId hostEntityId)
    {
        if (this.entityBroker is not { } broker)
        {
            return [];
        }

        var getResult = await broker.EntityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = hostEntityId }],
                Timestamps = [null],
            });
        var snapshot = getResult.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault(entity => entity.EntityId == hostEntityId);
        if (snapshot?.Data is { } data
            && data.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.GetArrayLength() > 0
            && names[0].ValueKind == JsonValueKind.Array)
        {
            return names[0]
                .EnumerateArray()
                .Where(component => component.ValueKind == JsonValueKind.String)
                .Select(component => component.GetString()!)
                .ToArray();
        }

        return [];
    }

    /// <summary>
    /// Builds the reverse-HTTP hub client factories this instance registers with. A Web-DAL client
    /// registers for reverse HTTP with its remote host endpoint so the host can call back into it
    /// (e.g. to route an agent to run on this client); the host's "Inbound Connections" panel reflects
    /// this registration. Non-web sources (Mongo/local/unknown) host their own data and never perform
    /// outbound reverse-registration, so they return an empty list. Registration is intentionally
    /// independent of <see cref="RemoteHostingConfiguration"/> and the dev tunnel, which govern inbound
    /// hosting/tunnel exposure rather than this outbound client registration.
    /// </summary>
    internal static IReadOnlyList<ReverseHttpClientTransportFactory> BuildReverseHttpHubFactories(
        RepositorySource repositorySource,
        EntityId localProfileEntityId)
    {
        if (repositorySource is WebRepositorySource web
            && !string.IsNullOrWhiteSpace(web.Endpoint))
        {
            return [new ReverseHttpClientTransportFactory(web.Endpoint, localProfileEntityId.ToString())];
        }

        return [];
    }

    private async Task InitializeWebHostAsync()
    {
        if (this.configuration is null)
        {
            return;
        }

        var hubFactories = BuildReverseHttpHubFactories(
            this.RepositorySource,
            this.entityBroker!.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId);

        var composition = new Services.WorkspacesTransportComposition(
            this.entityBroker!.EntityRepository.DataAccessLayer,
            this.entityBroker.EntityRepository.WorkspaceEntitySession,
            hubFactories);
        this.transportComposition = composition;
        this.trustedExecutorSelector.SetRemoteExecutor(composition.TrustedExecutor);
        await composition.StartAsync();
        this.webHost = new WorkspacesWebHost(composition.ConnectionStatusRegistry);
        this.ConnectionStatus = new ConnectionStatusViewModel(
            composition.ConnectionStatusRegistry,
            action => Dispatcher.UIThread.Post(action));

        if (this.configuration.RemoteHosting.Enabled && this.entityBroker is not null)
        {
            await this.webHost.StartAsync(
                this.configuration.RemoteHosting,
                this.entityBroker.EntityRepository.DataAccessLayer,
                this.logDirectoryProvider);
            this.ConnectionStatus.SetLocalAccessPoint(this.webHost.ListenUrl);
            this.StartDevTunnelHostIfConfigured(this.webHost.ListenUrl);
        }
    }

    private void StartDevTunnelHostIfConfigured(string? listenUrl)
    {
        var devTunnelConfiguration = this.configuration?.DevTunnel;
        if (devTunnelConfiguration is null
            || (string.IsNullOrWhiteSpace(devTunnelConfiguration.TunnelName)
                && string.IsNullOrWhiteSpace(devTunnelConfiguration.TunnelId)))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(listenUrl) || !Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri))
        {
            return;
        }

        this.ConnectionStatus?.SetTunnelName(devTunnelConfiguration.TunnelName);

        var localPort = listenUri.Port;
        var protocol = listenUri.Scheme;
        var hostService = new Services.DevTunnel.DevTunnelServiceFactory().CreateHostService();
        this.devTunnelHostService = hostService;
        hostService.StatusChanged += (_, status) => Dispatcher.UIThread.Post(
            () => this.ConnectionStatus?.SetDevTunnelStatus(status.State, status.AccessPointUrl, status.LastError));

        // Hosting runs in the background and surfaces progress/errors through the status event, so a
        // sign-in or relay failure never blocks GUI startup. The task is observed to avoid an
        // unobserved-exception escalation; the Error status already carries the failure detail.
        this.devTunnelHostStartTask = ObserveAsync(
            hostService.StartAsync(localPort, protocol, devTunnelConfiguration));

        static async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Surfaced via DevTunnelHostStatus.Error.
            }
        }
    }

    private void InitializeDockLayout()
    {
        var layout = this.dockFactory.CreateLayout();
        this.dockFactory.InitLayout(layout);
        this.Layout = layout;

        // Monitor workspace dock for closes
        var workspacesDock = FindDocumentDock(layout);
        if (workspacesDock?.VisibleDockables is System.Collections.Specialized.INotifyCollectionChanged collection)
        {
            collection.CollectionChanged += this.OnWorkspacesDockCollectionChanged;
        }
    }

    private void OnWorkspacesDockCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Re-derive the ORDER of WorkspacePanes from the workspace-tab host dock's VisibleDockables
        // for every structural change — including the Remove + Add pair that Dock.Avalonia's live
        // drag-reorder emits instead of a Move — so the Alt+Shift+N workspace-pane badges (and the
        // OnGoToWorkspacePaneAtIndex numbering, which read WorkspacePanes) always reflect the current
        // visual order. This mirrors the content-tab SyncPaneTabsFromDockChange (Option A) fix.
        //
        // This handler never removes panes: closes are handled exclusively by
        // RemoveWorkspacePaneAsync (via CloseWorkspaceCommand) and by
        // WorkspaceDockFactory.OnDockableClosed -> OnWorkspacePaneDockableClosed (the Dock close
        // button / CloseDockable), so a reorder Remove can never be mistaken for a close.
        if (this.suppressWorkspaceDockOrderSync)
        {
            return;
        }

        if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add
            or System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Move
            or System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            this.SyncWorkspacePanesOrderFromDock();
        }
    }

    /// <summary>
    /// Set while this view-model is programmatically reordering <see cref="WorkspacePanes"/> to
    /// match the workspace-tab host dock. The Dock library reflects those moves back onto
    /// <c>VisibleDockables</c>, which would re-enter <see cref="OnWorkspacesDockCollectionChanged"/>;
    /// the guard makes that reentrant call a no-op so the sync cannot recurse.
    /// </summary>
    private bool suppressWorkspaceDockOrderSync;

    private void RunSuppressingWorkspaceDockOrderSync(Action mutation)
    {
        var previous = this.suppressWorkspaceDockOrderSync;
        this.suppressWorkspaceDockOrderSync = true;
        try
        {
            mutation();
        }
        finally
        {
            this.suppressWorkspaceDockOrderSync = previous;
        }
    }

    /// <summary>
    /// Reorders <see cref="WorkspacePanes"/> to match the current visual order of the workspace-tab
    /// host dock's <c>VisibleDockables</c> (the root <c>WorkspacesPaneDock</c>). Uses index-based
    /// moves and only reorders panes present in both collections, so it is membership-safe and
    /// idempotent. Refreshes the Alt+Shift+N badge labels afterwards.
    /// </summary>
    private void SyncWorkspacePanesOrderFromDock()
    {
        if (this.Layout is null)
        {
            return;
        }

        var host = this.FindDocumentDock(this.Layout);
        if (host?.VisibleDockables is null)
        {
            return;
        }

        var newOrder = host.VisibleDockables
            .OfType<WorkspacePaneDocument>()
            .Select(d => d.WorkspacePane)
            .ToList();

        this.RunSuppressingWorkspaceDockOrderSync(() =>
        {
            for (var targetIndex = 0; targetIndex < newOrder.Count; targetIndex++)
            {
                var pane = newOrder[targetIndex];
                var currentIndex = this.WorkspacePanes.IndexOf(pane);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                {
                    this.WorkspacePanes.Move(currentIndex, targetIndex);
                }
            }
        });

    }

    /// <summary>
    /// Handles a workspace pane closed through the Dock UI (the tab close button, routed through
    /// <see cref="WorkspaceDockFactory.OnDockableClosed"/> / <c>CloseDockable</c>). Removing the
    /// dockable from <c>VisibleDockables</c> does not propagate back to the <see cref="WorkspacePanes"/>
    /// <c>ItemsSource</c>, so the pane must be removed here explicitly. Idempotent: no-op if the pane
    /// was already removed (e.g. via <see cref="CloseWorkspaceCommand"/>).
    /// </summary>
    internal void OnWorkspacePaneDockableClosed(WorkspacePaneDocument paneDoc)
        => _ = this.RemoveWorkspacePaneAsync(paneDoc.WorkspacePane);

    private async Task InitializeProfileAsync()
    {
        var profile = await this.profileStore.GetOrInitializeProfileAsync();
        this.ApplyProfile(profile);
    }

    internal async Task SetThemeAsync(
        string themeName)
    {
        var updatedProfile = await this.profileStore.ChangeProfileAsync(
            profile => profile with
            {
                Theme = ProfileThemeSettings.ForName(themeName),
            });
        this.ApplyProfile(updatedProfile);
    }

    private async Task SetDebuggingAsync(
        bool debugging)
    {
        var updatedProfile = await this.profileStore.ChangeProfileAsync(
            profile => profile with
            {
                Debugging = debugging,
            });
        this.ApplyProfile(updatedProfile);
    }

    private void ApplyProfile(
        ProfileSettings profile)
    {
        this.suppressThemeSelectionChange = true;
        this.SetProperty(ref this.selectedThemeName, profile.Theme.Name, nameof(this.SelectedThemeName));
        this.suppressThemeSelectionChange = false;

        var wrappedProfile = new Profile(profile);
        if (!this.SetProperty(ref this.currentProfile, wrappedProfile, nameof(this.CurrentProfile)))
        {
            this.ApplyThemeResources(profile.Theme);
            this.ApplyThemeVariant(profile.Theme.Name);
            this.RaisePropertyChanged(nameof(this.IsDebuggingEnabled));
            this.RaisePropertyChanged(nameof(this.IsDebuggingDisabled));
            this.RaisePropertyChanged(nameof(this.HasStickyParentContext));
            return;
        }

        this.ApplyThemeResources(profile.Theme);
        this.ApplyThemeVariant(profile.Theme.Name);
        this.RaisePropertyChanged(nameof(this.IsDebuggingEnabled));
        this.RaisePropertyChanged(nameof(this.IsDebuggingDisabled));
        this.RaisePropertyChanged(nameof(this.HasStickyParentContext));
    }

    private void ApplyThemeVariant(
        string themeName)
    {
        Application.Current!.RequestedThemeVariant = themeName.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void ApplyThemeResources(
        ProfileThemeSettings theme)
    {
        var resources = Application.Current!.Resources;

        // Font and class typography keys are user-customizable per-profile,
        // so they are written to the flat dict (correctly overriding theme defaults).
        // Theme-variant keys (Surface.*, Class.*.Foreground) are NOT written here;
        // they resolve through ThemeDictionaries (Light.axaml / Dark.axaml) so that
        // PopupRoot and secondary windows update correctly on theme switch.
        SetResource(resources, "Theme.FontFamily", new FontFamily(theme.Fonts.BaseFamily));
        SetResource(resources, "Theme.FontSize.Base", theme.Fonts.BaseSize * theme.Fonts.GlobalScale.Value);

        var classNames = new[] { "normal", "heading", "section-title", "caption", "muted", "accent" };
        foreach (var className in classNames)
        {
            this.ApplyClassResources(resources, className, theme.Classes.GetClass(className), theme);
        }
    }

    private void ApplyClassResources(
        Avalonia.Controls.IResourceDictionary resources,
        string className,
        ProfileThemeClass themeClass,
        ProfileThemeSettings theme)
    {
        SetResource(resources, $"Theme.Class.{className}.Opacity", themeClass.Opacity);
        SetResource(
            resources,
            $"Theme.Class.{className}.FontSize",
            theme.Fonts.BaseSize * theme.Fonts.GlobalScale.Value * themeClass.FontScale.Value);
        SetResource(resources, $"Theme.Class.{className}.FontWeight", ParseFontWeight(themeClass.FontWeight));
    }

    private static void SetBrushResource(
        Avalonia.Controls.IResourceDictionary resources,
        string key,
        string colorHex)
    {
        SetResource(resources, key, new SolidColorBrush(Color.Parse(colorHex)));
    }

    private static void SetResource(
        Avalonia.Controls.IResourceDictionary resources,
        string key,
        object value)
    {
        resources[key] = value;
    }

    private static FontWeight ParseFontWeight(
        string fontWeight)
    {
        return fontWeight switch
        {
            "Bold" => FontWeight.Bold,
            "SemiBold" => FontWeight.SemiBold,
            "Medium" => FontWeight.Medium,
            "Light" => FontWeight.Light,
            _ => FontWeight.Normal,
        };
    }

    private static bool ReadDebuggingParameter(
        object? parameter)
    {
        return parameter switch
        {
            bool boolParameter => boolParameter,
            string stringParameter => bool.TryParse(stringParameter, out var parsed) && parsed,
            _ => false,
        };
    }

    private WorkspacePaneViewModel CreatePlaceholderWorkspacePane(
        string paneId,
        string displayName,
        RelayCommand? closeCommand = null)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["entity", "workspace"],
              "display-name": "{{displayName}}"
            }
            """);

        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(Guid.Empty),
                ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
        return new WorkspacePaneViewModel(entity, paneId, closeCommand);
    }

    private void InitializeTopLevelViews()
    {
        var existingSelectionId = this.SelectedTopLevelView?.Id;
        var nextViews = new List<ViewDefinitionViewModel>();
        if (this.mainNavigationView?.Snapshot.Data is JsonElement mainViewData
            && mainViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (!this.EntityBroker.TryGetReferencedEntity(subView, "view-entity-id", out var viewEntity)
                    || viewEntity is null)
                {
                    continue;
                }

                nextViews.Add(CreateTopLevelView(viewEntity));
            }
        }

        nextViews.Add(
            new ViewDefinitionViewModel
            {
                Id = "entity-browser",
                Title = "Entity Browser",
                Description = "Dedicated browser/search (not view-driven).",
                IconGlyph = "⌕",
                IsEntityBrowser = true,
            });

        this.TopLevelViews.Clear();
        foreach (var viewDefinition in nextViews)
        {
            this.TopLevelViews.Add(viewDefinition);
        }

        if (this.TopLevelViews.Count == 0)
        {
            this.TopLevelViews.Add(EmptyView);
        }

        this.SelectedTopLevelView = this.TopLevelViews.FirstOrDefault(
            view => string.Equals(view.Id, existingSelectionId, StringComparison.Ordinal))
            ?? this.TopLevelViews[0];
    }

    private static ViewDefinitionViewModel CreateTopLevelView(
        SubscribedEntityViewModel viewEntity)
    {
        var title = "View";
        var description = "Repository view";
        if (viewEntity.Snapshot.Data is JsonElement viewData)
        {
            title = ReadLocalString(viewData, "title")
                ?? ReadLocalString(viewData, "display-name")
                ?? title;
            description = ReadPrimaryName(viewData) ?? description;
        }

        return new ViewDefinitionViewModel
        {
            Id = viewEntity.EntityId.ToString(),
            Title = title,
            Description = description,
            IconGlyph = "◻",
            ViewEntity = viewEntity,
        };
    }

    private async Task ApplySelectedViewAsync()
    {
        var next = new ViewPopulationViewModel();
        var previous = this.currentPopulation;
        this.CurrentViewPopulation = next;

        await previous.DisposeAsync();

        await this.PopulateViewAsync(next);
    }

    /// <summary>
    /// Rebinds a query-populated view in place — reusing the same <see cref="ViewPopulationViewModel"/>
    /// instead of disposing and recreating it — when a live query's membership changes. This is the
    /// live counterpart to navigate-away-and-back: the query subscription's results already reflect the
    /// change, so the view's entities are rebuilt from them without a full re-navigation.
    /// </summary>
    private async Task RebindPopulationAsync(ViewPopulationViewModel population)
    {
        // A population that is no longer current (already replaced by a navigation) must not resurrect
        // itself; only the displayed population rebinds.
        if (!ReferenceEquals(this.currentPopulation, population)
            || population.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        population.PrepareForRebuild();
        await this.PopulateViewAsync(population);
    }

    private async Task PopulateViewAsync(ViewPopulationViewModel next)
    {
        var selectedView = this.selectedTopLevelView ?? EmptyView;
        if (string.Equals(selectedView.Id, EmptyView.Id, StringComparison.Ordinal))
        {
            this.StickyParentContextText = string.Empty;
            return;
        }

        if (next.CancellationToken.IsCancellationRequested) return;

        if (selectedView.IsEntityBrowser)
        {
            await this.OpenEntityBrowserTabAsync();
            this.StickyParentContextText = string.Empty;
            return;
        }

        if (next.CancellationToken.IsCancellationRequested) return;

        if (selectedView.ViewEntity is not SubscribedEntityViewModel selectedViewEntity)
        {
            this.StickyParentContextText = selectedView.Title;
            return;
        }

        if (selectedViewEntity.Snapshot.Data is not JsonElement selectedViewData)
        {
            this.StickyParentContextText = selectedView.Title;
            return;
        }

        var associatedNoteEntity = await this.LoadAssociatedViewNoteAsync(next, selectedViewData);
        if (next.CancellationToken.IsCancellationRequested) return;

        if (associatedNoteEntity is not null)
        {
            var viewEntity = await this.CreateViewEntityViewModelAsync(associatedNoteEntity, indentLevel: 0, isParentContext: true);
            next.Entities.Add(viewEntity);
            next.RootEntities.Add(viewEntity);
        }

        await this.LoadSubViewEntitiesAsync(selectedViewData);

        if (next.CancellationToken.IsCancellationRequested) return;

        if (selectedViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (next.CancellationToken.IsCancellationRequested) return;

                if (this.EntityBroker.TryGetReferencedEntity(subView, "view-entity-id", out var subViewEntity)
                    && subViewEntity is not null)
                {
                    var viewEntity = await this.CreateViewEntityViewModelAsync(subViewEntity, indentLevel: 0);
                    next.Entities.Add(viewEntity);
                    next.RootEntities.Add(viewEntity);
                    continue;
                }

                if (TryReadSubViewGetRequest(subView, out var getRequest))
                {
                    var getEntities = await this.LoadGetSubViewEntitiesAsync(next, getRequest);
                    if (next.CancellationToken.IsCancellationRequested) return;
                    await this.AddSubViewEntitiesWithHierarchyAsync(next, getEntities);
                    if (next.CancellationToken.IsCancellationRequested) return;
                    continue;
                }

                if (TryReadSubViewQueryRequest(subView, out var queryRequest))
                {
                    var queryEntities = await this.LoadQuerySubViewEntitiesAsync(next, queryRequest);
                    if (next.CancellationToken.IsCancellationRequested) return;
                    await this.AddSubViewEntitiesWithHierarchyAsync(next, queryEntities);
                    if (next.CancellationToken.IsCancellationRequested) return;
                }
            }
        }

        if (next.CancellationToken.IsCancellationRequested) return;

        this.StickyParentContextText = $"Parent Context: {selectedView.Title}";
    }

    private async Task LoadSubViewEntitiesAsync(
        JsonElement viewData)
    {
        if (!viewData.TryGetProperty("sub-views", out var subViews)
            || subViews.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var requests = new List<GetEntityRequest>();
        foreach (var subView in subViews.EnumerateArray())
        {
            if (TryReadEntityRequest(subView, "view-entity-id", out var request))
            {
                requests.Add(request);
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        await this.EntityBroker!.GetEntitiesAsync(requests);
    }

    private async Task<IReadOnlyList<SubscribedEntityViewModel>> LoadGetSubViewEntitiesAsync(
        ViewPopulationViewModel population,
        GetRequest getRequest)
    {
        var subscribedGet = await this.EntityBroker.SubscribeGetAsync(getRequest);
        population.AddGetSubscription(subscribedGet);

        if (subscribedGet.Results.Count == 0)
        {
            return Array.Empty<SubscribedEntityViewModel>();
        }

        return subscribedGet.Results.ToArray();
    }

    private async Task<IReadOnlyList<SubscribedEntityViewModel>> LoadQuerySubViewEntitiesAsync(
        ViewPopulationViewModel population,
        QueryRequest queryRequest)
    {
        // Exclude not-interesting targets at the query level (via a join) unless the user opts to show
        // hidden items.
        var effectiveQuery = this.ShowHiddenItems
            ? queryRequest
            : NotInterestingQuery.ExcludingNotInteresting(queryRequest);

        // Also fetch each matched entity's interest relationships so its badge glyphs can be rendered.
        effectiveQuery = WithInterestRelationships(effectiveQuery, this.interestCatalog);

        var subscribedQuery = await this.EntityBroker.SubscribeQueryAsync(effectiveQuery);
        population.AddQuerySubscription(subscribedQuery, () => this.RebindPopulationAsync(population));

        if (subscribedQuery.Results.Count == 0)
        {
            return Array.Empty<SubscribedEntityViewModel>();
        }

        return subscribedQuery.Results.ToArray();
    }

    internal static QueryRequest WithInterestRelationships(QueryRequest query, InterestCatalog? catalog)
    {
        return query with
        {
            RelationshipsToReturn =
            [
                ..(query.RelationshipsToReturn ?? []),
                new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet(["related"]) },
                ..(catalog is { InterestTypeNames.Count: > 0 } validCatalog
                    ? [new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet([.. validCatalog.InterestTypeNames]) }]
                    : Array.Empty<GetRelationshipRequest>()),
            ],
        };
    }

    private async Task<SubscribedEntityViewModel?> LoadAssociatedViewNoteAsync(
        ViewPopulationViewModel population,
        JsonElement selectedViewData)
    {
        if (!TryReadPrimaryEntityName(selectedViewData, out var selectedViewName))
        {
            return null;
        }

        var noteSubscription = await this.EntityBroker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = selectedViewName,
                        EntityTypeNames = new EntityTypeNameSet(["note"]),
                    },
                ],
                Timestamps = [null],
            });
        population.AddGetSubscription(noteSubscription);
        return noteSubscription.Results.FirstOrDefault();
    }

    private bool CanActivateShortcut(
        object? parameter)
    {
        return parameter is EntityShortcutViewModel entityShortcut
            && entityShortcut.IsEnabled;
    }

    private async Task OnActivateShortcutAsync(
        object? parameter)
    {
        if (parameter is not EntityShortcutViewModel entityShortcut)
        {
            return;
        }

        entityShortcut.IsEnabled = false;
        this.ActivateShortcutCommand.RaiseCanExecuteChanged();
        try
        {
            await entityShortcut.HandleAsync(this);
        }
        finally
        {
            entityShortcut.IsEnabled = true;
            this.ActivateShortcutCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Invoked when an entity card is clicked. Resolves the clicked entity and runs the
    /// (unregistered) <see cref="EntityClickShortcutHandler"/>, which opens configured entity types.
    /// </summary>
    public Task<bool> ActivateEntityClickAsync(SubscribedEntityViewModel entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return this.entityClickShortcutHandler is { } handler
            ? handler.Handle(this, Shortcut.Open, entity)
            : Task.FromResult(false);
    }

    /// <summary>
    /// Opens the entity with the supplied id (used to navigate from a rendered entity-reference field,
    /// for example a relationship's participants).
    /// </summary>
    public async Task OpenEntityByIdAsync(string entityId)
    {
        if (this.entityBroker is null || string.IsNullOrWhiteSpace(entityId))
        {
            return;
        }

        EntityId id;
        try
        {
            id = new EntityId(entityId);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return;
        }

        var entities = await this.entityBroker.GetEntitiesAsync(new[] { id });
        var entity = entities.FirstOrDefault(candidate => candidate.EntityId == id) ?? entities.FirstOrDefault();
        if (entity is not null)
        {
            await this.ActivateEntityClickAsync(entity);
        }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SubscribedEntityViewModel, EntityListNodeViewModel> cardNodesByEntity = new();

    /// <summary>
    /// Registers the card node that renders the supplied entity. Retained so card nodes can be
    /// looked up by entity when needed.
    /// </summary>
    public void RegisterCardNode(SubscribedEntityViewModel entity, EntityListNodeViewModel cardNode)
    {
        this.cardNodesByEntity.AddOrUpdate(entity, cardNode);
    }

    /// <summary>Finds the card node currently rendering the supplied entity, if any.</summary>
    public EntityListNodeViewModel? FindCardNode(SubscribedEntityViewModel entity)
    {
        return this.cardNodesByEntity.TryGetValue(entity, out var cardNode) ? cardNode : null;
    }

    private async Task OnActivateEntityClickAsync(
        object? parameter)
    {
        var entity = parameter switch
        {
            ViewEntityViewModel viewEntity => viewEntity.Entity,
            SubscribedEntityViewModel subscribedEntity => subscribedEntity,
            _ => null,
        };

        if (entity is null)
        {
            return;
        }

        await this.ActivateEntityClickAsync(entity);
    }

    /// <summary>
    /// Toggles an interest on or off for an entity when its badge glyph is clicked: removes the
    /// existing interest relationship of that type targeting the entity, or creates one (targeting the
    /// entity, by the current user) when none exists.
    /// </summary>
    private async Task<ViewEntityViewModel> CreateViewEntityViewModelAsync(
        SubscribedEntityViewModel entity,
        int indentLevel,
        bool isExpanded = true,
        bool isParentContext = false)
    {
        // Project the entity's interests (from its loaded relationships) into toggleable badge glyphs.
        if (this.interestCatalog is { } interestCatalog && this.entityTypeCatalog is { } entityTypeCatalog)
        {
            entity.Badges.SetBadges(InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity.Snapshot));

            // Re-project interest badges whenever the entity's snapshot changes so relationship-only
            // updates (for example toggling an interest, which the broker now pushes live) refresh the
            // badges without requiring the user to navigate away and back.
            entity.PropertyChanged += (_, e) =>
            {
                if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Snapshot), StringComparison.Ordinal))
                {
                    entity.Badges.SetBadges(InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity.Snapshot));
                }
            };
        }

        // Project the entity's annotated status fields into colored status badges. Discovery is
        // asynchronous (each field's status annotation is resolved through the schema), so the badges
        // arrive after the card is created and populate the entity's observable status-badge model.
        _ = this.PopulateStatusBadgesAsync(entity);

        var vm = new ViewEntityViewModel(
            entity,
            this,
            this.shortcutManager,
            indentLevel,
            isExpanded: this.expandedEntityIds.TryGetValue(entity.EntityId.ToString(), out var storedExpanded) ? storedExpanded : isExpanded,
            isParentContext: isParentContext,
            fieldEditorFactory: this.fieldEditorFactory);
        await vm.InitializeAsync();

        // Persist expansion state changes; the tree template shows/hides cached children locally.
        var entityIdStr = entity.EntityId.ToString();
        vm.PropertyChanged += (sender, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ViewEntityViewModel.IsExpanded), StringComparison.Ordinal)
                && sender is ViewEntityViewModel toggled)
            {
                this.expandedEntityIds[entityIdStr] = toggled.IsExpanded;
            }
        };

        return vm;
    }

    /// <summary>
    /// Asynchronously builds the entity's status badges (one per annotated status field across its
    /// entity types) and applies them to the entity's status-badge model. Resolving the schema for
    /// each field is asynchronous, so this runs after the card is created; the badges flow to the card
    /// through the entity's observable status-badge model.
    /// </summary>
    private async Task PopulateStatusBadgesAsync(
        SubscribedEntityViewModel entity)
    {
        if (this.fieldEditorFactory is not { } fieldEditorFactory
            || entity.Snapshot.Data is not JsonElement entityData
            || entityData.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var statusBadges = await fieldEditorFactory
            .BuildStatusBadgesAsync(entityData)
            .ConfigureAwait(true);

        entity.StatusBadges.SetBadges(statusBadges);
    }

    /// <summary>
    /// Adds a sub-view's root entities to the view, assembling each entity's declared hierarchy
    /// (related members grouped under contextual parents) and flattening it depth-first into the
    /// indented entity list. Entities whose types declare no traversals render flat (indent 0).
    /// </summary>
    private async Task AddSubViewEntitiesWithHierarchyAsync(
        ViewPopulationViewModel population,
        IReadOnlyList<SubscribedEntityViewModel> rootEntities)
    {
        var hierarchy = await new ViewHierarchyAssembler(this.EntityBroker).AssembleAsync(rootEntities);
        foreach (var node in hierarchy)
        {
            await this.AddHierarchyNodeAsync(population, node, indentLevel: 0);
        }
    }

    private async Task AddHierarchyNodeAsync(
        ViewPopulationViewModel population,
        ViewHierarchyNode node,
        int indentLevel,
        ViewEntityViewModel? parent = null)
    {
        ViewEntityViewModel? vm = null;
        if (!node.IsAncestorGroup)
        {
            vm = await this.CreateViewEntityViewModelAsync(node.Entity!, indentLevel, isExpanded: node.IsExpanded);
            if (node.Children.Count > 0)
            {
                vm.HasTraversedChildren = true;
            }

            if (parent is null)
            {
                population.Entities.Add(vm);
                population.RootEntities.Add(vm);
            }
            else
            {
                population.Entities.Add(vm);
                parent.AddChild(vm);
            }
        }

        if (vm is null || node.Children.Count > 0)
        {
            foreach (var child in node.Children)
            {
                await this.AddHierarchyNodeAsync(population, child, indentLevel + 1, vm ?? parent);
            }
        }
    }

    private async void OnRefreshTick(
        object? sender,
        EventArgs e)
    {
        await this.EntityBroker.RefreshAsync();
    }

    private void OnEntityBrokerChanged(
        object? sender,
        EntityBrokerChangedEventArgs e)
    {
        var navigationEntityChanged = this.mainNavigationView is not null
            && e.ChangedEntityIds.Contains(this.mainNavigationView.EntityId);

        if (navigationEntityChanged)
        {
            this.InitializeTopLevelViews();
        }

        if (navigationEntityChanged || e.HasQueryMembershipChanges)
        {
            _ = this.ApplySelectedViewAsync();
        }
    }

    private void OnInterestCatalogChanged(object? sender, EventArgs e)
    {
        // Interest types changed - refresh the current view to update badge glyphs
        _ = this.ApplySelectedViewAsync();
    }

    private void OnEntityTypeCatalogChanged(object? sender, EventArgs e)
    {
        // Entity types changed - refresh the current view to update badge filtering
        _ = this.ApplySelectedViewAsync();
    }

    internal async Task OpenWorkspaceAsync(
        GetEntityRequest workspaceRequest)
    {
        var (loadingWorkspacePane, alreadyOpening) = this.GetOrCreateLoadingWorkspacePane(workspaceRequest);
        this.SelectedWorkspacePane = loadingWorkspacePane;

        // If a load for this same workspace request is already in progress, ignore the
        // duplicate open request so the workspace is only opened once (see issue #23).
        if (alreadyOpening)
        {
            return;
        }

        var workspaceSnapshot = await this.LoadSingleEntitySnapshotAsync(workspaceRequest);
        if (workspaceSnapshot?.Data is not JsonElement workspaceData)
        {
            this.DismissLoadingPane(loadingWorkspacePane);
            return;
        }

        // Check if workspace is already open
        var existingWorkspace = this.WorkspacePanes.FirstOrDefault(
            pane => string.Equals(pane.Id, workspaceSnapshot.EntityId.ToString(), StringComparison.Ordinal));
        if (existingWorkspace is not null)
        {
            if (this.WorkspacePanes.Contains(loadingWorkspacePane))
            {
                this.WorkspacePanes.Remove(loadingWorkspacePane);
            }

            this.SelectedWorkspacePane = existingWorkspace;
            // Activate the workspace document in the dock
            if (this.Layout is not null)
            {
                var workspacesDock = FindDocumentDock(this.Layout);
                var existingDocument = workspacesDock?.VisibleDockables
                    ?.OfType<WorkspacePaneDocument>()
                    .FirstOrDefault(doc => doc.WorkspacePane == existingWorkspace);
                if (existingDocument is not null)
                {
                    this.dockFactory.SetActiveDockable(existingDocument);
                }
            }
            return;
        }

        // Fetch just the workspace entity to build the skeleton pane
        var workspaceEntities = await this.EntityBroker!.GetEntitiesAsync([workspaceRequest]);
        var workspaceEntity = workspaceEntities.FirstOrDefault(e => e.EntityId == workspaceSnapshot.EntityId);
        if (workspaceEntity is null)
        {
            this.DismissLoadingPane(loadingWorkspacePane);
            return;
        }

        // Phase 1: create skeleton workspace pane and show it immediately
        var workspacePane = new WorkspacePaneViewModel(workspaceEntity, null, this.CloseWorkspaceCommand, this.SaveWorkspacePaneAsync);
        workspacePane.ContentLayout = this.dockFactory.CreateWorkspaceContentLayout(workspacePane);
        this.SubscribeToInnerDockChanges(workspacePane);

        var loadingPaneIndex = this.WorkspacePanes.IndexOf(loadingWorkspacePane);
        if (loadingPaneIndex >= 0)
        {
            this.WorkspacePanes[loadingPaneIndex] = workspacePane;
        }
        else
        {
            this.WorkspacePanes.Add(workspacePane);
        }

        this.AddWorkspacePaneToDock(workspacePane);
        this.SelectedWorkspacePane = workspacePane;

        // Phase 2: populate tabs asynchronously (fire and forget)
        var populateTask = this.PopulateWorkspacePaneTabsAsync(workspacePane, workspaceEntity, workspaceData);
        _ = populateTask.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                workspacePane.SignalPopulated(t.Exception.GetBaseException());
            }
            else
            {
                workspacePane.SignalPopulated();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Removes a transient loading pane from <see cref="WorkspacePanes"/> when the workspace
    /// entity could not be loaded (not found or missing data). Restores the default placeholder
    /// if no workspace panes remain.
    /// </summary>
    private void DismissLoadingPane(WorkspacePaneViewModel loadingPane)
    {
        var paneIndex = this.WorkspacePanes.IndexOf(loadingPane);
        if (paneIndex < 0)
        {
            return;
        }

        this.WorkspacePanes.RemoveAt(paneIndex);

        if (this.SelectedWorkspacePane != loadingPane)
        {
            return;
        }

        if (this.WorkspacePanes.Count > 0)
        {
            this.SelectedWorkspacePane = this.WorkspacePanes[Math.Min(paneIndex, this.WorkspacePanes.Count - 1)];
        }
        else
        {
            var placeholder = this.CreatePlaceholderWorkspacePane(DefaultWorkspaceId, "No workspace selected.");
            this.WorkspacePanes.Add(placeholder);
            this.SelectedWorkspacePane = placeholder;
        }
    }

    private void AddWorkspacePaneToDock(WorkspacePaneViewModel workspacePane)
    {
        if (this.Layout is null)
        {
            return;
        }

        // WorkspacesPaneDock uses ItemsSource = WorkspacePanes, so the pane document
        // is created automatically when the pane is added to WorkspacePanes.
        // We only need to activate it here.
        var workspacesDock = FindDocumentDock(this.Layout);
        if (workspacesDock is not null)
        {
            var existingDocument = this.dockFactory.GetPaneDocument(workspacePane.Id)
                ?? workspacesDock.VisibleDockables
                    ?.OfType<WorkspacePaneDocument>()
                    .FirstOrDefault(doc => doc.WorkspacePane == workspacePane);
            if (existingDocument is not null)
            {
                this.dockFactory.SetActiveDockable(existingDocument);
            }
        }
    }

    private bool CanCloseWorkspace(object? parameter)
    {
        if (parameter is not WorkspacePaneViewModel pane)
        {
            return false;
        }

        // Can't close the default placeholder workspace
        return !string.Equals(pane.Id, DefaultWorkspaceId, StringComparison.Ordinal);
    }

    public async Task CloseWorkspacePaneAsync(WorkspacePaneViewModel pane)
    {
        await this.RemoveWorkspacePaneAsync(pane);
    }

    private async void OnCloseWorkspace(object? parameter)
    {
       if (parameter is not WorkspacePaneViewModel pane)
       {
           return;
       }

       await this.CloseWorkspacePaneAsync(pane);
    }

    private void OnCloseActiveTab()
    {
        if (this.selectedWorkspacePane?.ContentLayout is null)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(this.selectedWorkspacePane.ContentLayout);
        if (documentDock?.ActiveDockable is not WorkspaceDocument activeDoc)
        {
            return;
        }

        var pane = this.selectedWorkspacePane;
        var tab = activeDoc.TabViewModel;
        if (tab is null) return;
        // Removing from pane.Tabs removes the WorkspaceDocument via ItemsSource automatically.
        pane.Tabs.Remove(tab);
        _ = DisposeWorkspaceTabAsync(tab);
    }

    private void OnCycleTab(int delta)
    {
        var pane = this.selectedWorkspacePane;
        if (pane is null || pane.ContentLayout is null || pane.Tabs.Count < 2)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(pane.ContentLayout);
        if (documentDock is null)
        {
            return;
        }

        // Fix #1107: cycle in dock visual order (VisibleDockables), not pane.Tabs order.
        // pane.Tabs is a plain, order-independent membership set; the visual/dock order is the
        // source of truth for user-facing tab cycling.
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

        this.dockFactory.SetActiveDockable(nextDoc);
        this.dockFactory.SetFocusedDockable(documentDock, nextDoc);
        this.notificationService.MarkRead(nextDoc.Id);
    }

    private void OnNavigateBack()
    {
        this.navigatingViaHistory = true;
        try
        {
            if (this.navigationHistoryService.GoBackSkipping(this.IsTabOpen, out var entry) && entry is not null)
            {
                this.ActivateTabById(entry.TabId, entry.WorkspacePaneId);
            }
        }
        finally
        {
            this.navigatingViaHistory = false;
        }
    }

    private void OnNavigateForward()
    {
        this.navigatingViaHistory = true;
        try
        {
            if (this.navigationHistoryService.GoForwardSkipping(this.IsTabOpen, out var entry) && entry is not null)
            {
                this.ActivateTabById(entry.TabId, entry.WorkspacePaneId);
            }
        }
        finally
        {
            this.navigatingViaHistory = false;
        }
    }

    private bool IsTabOpen(NavigationEntry entry)
    {
        if (entry.WorkspacePaneId is not null)
        {
            var targetPane = this.WorkspacePanes.FirstOrDefault(
                p => string.Equals(p.Id, entry.WorkspacePaneId, StringComparison.Ordinal));
            if (targetPane?.Tabs.Any(t => string.Equals(t.Id, entry.TabId, StringComparison.Ordinal)) == true)
            {
                return true;
            }
        }

        return this.WorkspacePanes.Any(
            pane => pane.Tabs.Any(t => string.Equals(t.Id, entry.TabId, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Searches all workspace panes for a tab with the given <paramref name="tabId"/>, switches
    /// <see cref="SelectedWorkspacePane"/> to the pane that contains it, and activates the tab.
    /// If <paramref name="workspacePaneId"/> is supplied and the target pane is not currently open,
    /// the workspace is opened first via <see cref="OpenWorkspaceAsync"/> before attempting
    /// tab activation.
    /// </summary>
    private void ActivateTabById(string tabId, string? workspacePaneId)
    {
        _ = this.ActivateTabByIdAsync(tabId, workspacePaneId);
    }

    /// <summary>
    /// #1135: Cross-workspace navigation entry-point for the running-agent brain popup's
    /// fallback rows (rows whose session has no open tab in any pane). Switches to (and, if
    /// necessary, loads) the workspace pane the session was started in, then opens/focuses
    /// the agent tab there. Safe no-op when the session is unknown or has no resolvable
    /// owning workspace/entity.
    /// </summary>
    internal async Task OpenAgentForSessionAsync(string sessionKey)
    {
        if (this.runningAgentChats is null)
        {
            return;
        }

        var session = this.runningAgentChats.RunningSessions
            .FirstOrDefault(s => string.Equals(s.SessionId.Value, sessionKey, StringComparison.Ordinal));
        if (session is null)
        {
            return;
        }

        if (session.WorkspaceId is { } paneId
            && !string.IsNullOrWhiteSpace(paneId)
            && Guid.TryParse(paneId, out var paneGuid))
        {
            var pane = this.WorkspacePanes.FirstOrDefault(
                p => string.Equals(p.Id, paneId, StringComparison.Ordinal));
            if (pane is null || pane.ContentLayout is null)
            {
                await this.OpenWorkspaceAsync(new GetEntityRequest { EntityId = new EntityId(paneGuid) });
                pane = this.WorkspacePanes.FirstOrDefault(
                    p => string.Equals(p.Id, paneId, StringComparison.Ordinal));
            }

            if (pane is not null)
            {
                this.SelectedWorkspacePane = pane;
            }
        }

        if (session.EntityId is { } eid && !string.IsNullOrWhiteSpace(eid))
        {
            await this.OpenEntityByIdAsync(eid);
        }
    }

    internal async Task ActivateTabByIdAsync(string tabId, string? workspacePaneId)
    {
        // Prefer the workspace pane recorded in the history entry
        if (workspacePaneId is not null)
        {
            var targetPane = this.WorkspacePanes.FirstOrDefault(
                p => string.Equals(p.Id, workspacePaneId, StringComparison.Ordinal));

            // If the pane is not open yet, open it first
            if ((targetPane is null || targetPane.ContentLayout is null)
                && Guid.TryParse(workspacePaneId, out var paneGuid))
            {
                await this.OpenWorkspaceAsync(new GetEntityRequest { EntityId = new EntityId(paneGuid) });
                targetPane = this.WorkspacePanes.FirstOrDefault(
                    p => string.Equals(p.Id, workspacePaneId, StringComparison.Ordinal));
            }

            if (targetPane is not null)
            {
                var tab = targetPane.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
                if (tab is not null)
                {
                    var doc = this.dockFactory.GetDocumentForTab(tabId);
                    if (doc is not null && targetPane.ContentLayout is not null)
                    {
                        var documentDock = this.FindDocumentDock(targetPane.ContentLayout);
                        this.SelectedWorkspacePane = targetPane;
                        this.dockFactory.SetActiveDockable(doc);
                        if (documentDock is not null)
                            this.dockFactory.SetFocusedDockable(documentDock, doc);
                        return;
                    }
                }

                if (targetPane.ContentLayout is not null)
                {
                    // Tab not yet in pane.Tabs (async population in progress after workspace open).
                    // Subscribe and activate the tab once it appears.
                    this.ActivateTabWhenLoaded(targetPane, tabId);
                    return;
                }
            }
        }

        // Fall back to searching all panes
        foreach (var pane in this.WorkspacePanes)
        {
            var tab = pane.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
            if (tab is null) continue;

            var doc = this.dockFactory.GetDocumentForTab(tabId);
            if (doc is null || pane.ContentLayout is null) continue;

            var dock = this.FindDocumentDock(pane.ContentLayout);
            this.SelectedWorkspacePane = pane;
            this.dockFactory.SetActiveDockable(doc);
            if (dock is not null)
                this.dockFactory.SetFocusedDockable(dock, doc);
            return;
        }
    }

    private void ActivateTabWhenLoaded(WorkspacePaneViewModel pane, string tabId)
    {
        void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var tab = pane.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
            if (tab is null) return;

            pane.Tabs.CollectionChanged -= OnTabsCollectionChanged;

            var doc = this.dockFactory.GetDocumentForTab(tabId);
            if (doc is null || pane.ContentLayout is null) return;

            var documentDock = this.FindDocumentDock(pane.ContentLayout);
            this.SelectedWorkspacePane = pane;
            this.dockFactory.SetActiveDockable(doc);
            if (documentDock is not null)
                this.dockFactory.SetFocusedDockable(documentDock, doc);
        }

        pane.Tabs.CollectionChanged += OnTabsCollectionChanged;

        // Race-condition guard: check again after subscribing
        var existing = pane.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (existing is not null)
        {
            pane.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            var doc = this.dockFactory.GetDocumentForTab(tabId);
            if (doc is null || pane.ContentLayout is null) return;
            var documentDock = this.FindDocumentDock(pane.ContentLayout);
            this.SelectedWorkspacePane = pane;
            this.dockFactory.SetActiveDockable(doc);
            if (documentDock is not null)
                this.dockFactory.SetFocusedDockable(documentDock, doc);
        }
    }

    internal async Task RemoveWorkspacePaneAsync(WorkspacePaneViewModel pane)
    {// Don't allow closing the default placeholder
       if (string.Equals(pane.Id, DefaultWorkspaceId, StringComparison.Ordinal))
       {
           return;
       }

       var paneIndex = this.WorkspacePanes.IndexOf(pane);
       if (paneIndex < 0)
       {
           return;
       }

       this.UnsubscribeFromInnerDockChanges(pane);
       this.WorkspacePanes.RemoveAt(paneIndex);

       // If we just closed the selected workspace, select another one
       if (this.SelectedWorkspacePane == pane)
       {
           // Try to select the workspace at the same index, or the last one
           if (paneIndex < this.WorkspacePanes.Count)
           {
               this.SelectedWorkspacePane = this.WorkspacePanes[paneIndex];
           }
           else if (this.WorkspacePanes.Count > 0)
           {
               this.SelectedWorkspacePane = this.WorkspacePanes[this.WorkspacePanes.Count - 1];
           }
           else
           {
               // If no workspaces left, open the getting-started workspace
               await this.OpenGettingStartedWorkspaceAsync();
           }
       }
    }

    private (WorkspacePaneViewModel Pane, bool AlreadyOpening) GetOrCreateLoadingWorkspacePane(
        GetEntityRequest workspaceRequest)
    {
        var paneId = $"{LoadingWorkspaceIdPrefix}{GetWorkspaceRequestKey(workspaceRequest)}";
        var existingPane = this.WorkspacePanes.FirstOrDefault(
            pane => string.Equals(pane.Id, paneId, StringComparison.Ordinal));
        if (existingPane is not null)
        {
            return (existingPane, true);
        }

        var placeholderPane = this.WorkspacePanes.FirstOrDefault(
            pane => string.Equals(pane.Id, DefaultWorkspaceId, StringComparison.Ordinal));
        if (placeholderPane is not null)
        {
            this.WorkspacePanes.Remove(placeholderPane);
        }

        var displayName = $"Loading {GetWorkspaceRequestDisplayText(workspaceRequest)}...";
        var loadingPane = this.CreatePlaceholderWorkspacePane(paneId, displayName, this.CloseWorkspaceCommand);
        this.WorkspacePanes.Add(loadingPane);
        return (loadingPane, false);
    }

    internal async Task OpenEntityTabAsync(
        GetEntityRequest entityRequest,
        bool focus = true)
    {
        var entities = await this.EntityBroker!.GetEntitiesAsync([entityRequest]);
        var subscribedEntity = entities.FirstOrDefault();
        if (subscribedEntity is null)
        {
            return;
        }

        await this.OpenTabAsync(
            new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog, this)
            {
                Id = subscribedEntity.EntityId.ToString(),
                Title = subscribedEntity.DisplayName,
                Entity = subscribedEntity,
            },
            focus: focus);
    }

    public async Task OpenTabAsync(WorkspaceTabViewModel tab, string? insertAfterTabId = null, bool focus = true, string? workspacePaneId = null)
    {
        // Ensure we have a real workspace loaded (not the placeholder)
        await this.EnsureWorkspaceLoadedAsync();

        var targetPane = workspacePaneId is not null
            ? this.WorkspacePanes.FirstOrDefault(p => string.Equals(p.Id, workspacePaneId, StringComparison.Ordinal))
                ?? this.selectedWorkspacePane
            : this.selectedWorkspacePane;

        if (targetPane?.ContentLayout is null)
        {
            return;
        }

        // Find the document dock in the target workspace's ContentLayout
        var documentDock = this.FindDocumentDock(targetPane.ContentLayout);
        if (documentDock is null)
        {
            return;
        }

        // Check if tab already exists
        var existingDocument = this.dockFactory.GetDocumentForTab(tab.Id);

        if (existingDocument is not null)
        {
            // Already exists, just activate it
            if (!ReferenceEquals(existingDocument.TabViewModel, tab))
            {
                _ = DisposeWorkspaceTabAsync(tab);
            }
            if (focus)
            {
                if (!ReferenceEquals(this.selectedWorkspacePane, targetPane))
                {
                    this.SelectedWorkspacePane = targetPane;
                }
                this.dockFactory.SetActiveDockable(existingDocument);
                this.notificationService.MarkRead(tab.Id);
                this.dockFactory.SetFocusedDockable(documentDock, existingDocument);
                // Set SelectedTab directly so GoToPane notification-read works even when the
                // ItemsSource/ItemContainerGenerator pipeline is inactive (e.g. headless tests).
                targetPane.SelectedTab = existingDocument.TabViewModel;
                if (!this.navigatingViaHistory)
                {
                    this.navigationHistoryService.Push(new NavigationEntry(tab.Id, targetPane.Id));
                }
            }
            return;
        }

        // Fix #1065: New tabs opened while an existing tab is active should appear one
        // position to the right of that originator within the SAME visual tab strip
        // (browser-style), instead of being appended to the end.
        //
        // The correct layer for this is the Dock DocumentDock that actually hosts the
        // source tab (its VisibleDockables), NOT the flat WorkspacePaneViewModel.Tabs
        // list. In a split layout, Tabs no longer maps 1:1 to a single strip, so
        // inserting into Tabs would target the wrong strip. WorkspacePaneViewModel.Tabs
        // is treated as an order-independent membership set (per #1107); the dock's
        // VisibleDockables is the sole source of visual order.
        //
        // Anchor resolution: an explicit insertAfterTabId always wins; otherwise the
        // currently selected tab in the target pane is the originator.
        var anchorTabId = insertAfterTabId
            ?? targetPane.SelectedTab?.Id;
        var sourceDocument = anchorTabId is not null
            ? this.dockFactory.GetDocumentForTab(anchorTabId)
            : null;
        var sourceDock = sourceDocument?.Owner as IDock;

        if (focus && !ReferenceEquals(this.selectedWorkspacePane, targetPane))
        {
            this.SelectedWorkspacePane = targetPane;
        }

        // Add to the pane's Tabs membership set. The ItemsSource generator on the
        // pane's WorkspaceContentDock creates a WorkspaceDocument and appends it to
        // that dock's VisibleDockables.
        targetPane.Tabs.Add(tab);
        var newDocument = this.dockFactory.GetDocumentForTab(tab.Id);

        // If we have a valid source anchor, move the newly-created dockable to
        // sourceIndex + 1 within the source's owning DocumentDock. This is deterministic
        // regardless of whether the source lives in the ItemsSource-bound dock or in a
        // split-off dock, and clamps naturally when the source is last in its strip.
        var placementDock = documentDock;
        if (sourceDocument is not null
            && sourceDock is not null
            && sourceDock.VisibleDockables is not null
            && newDocument is not null)
        {
            var sourceIndex = sourceDock.VisibleDockables.IndexOf(sourceDocument);
            if (sourceIndex >= 0)
            {
                // Detach the new document from wherever the generator put it (may be
                // sourceDock itself in the single-strip case, or a different dock in
                // the split-off case), then insert at sourceIndex + 1 within sourceDock.
                this.dockFactory.RemoveDockable(newDocument, collapse: false);

                // Re-resolve after remove: if sourceDock == prior owner and the new
                // document was appended at the end, sourceIndex is unchanged.
                var updatedSourceIndex = sourceDock.VisibleDockables.IndexOf(sourceDocument);
                if (updatedSourceIndex < 0)
                {
                    // Source vanished during remove (defensive); fall back to append.
                    this.dockFactory.AddDockable(sourceDock, newDocument);
                }
                else
                {
                    var targetIndex = Math.Min(updatedSourceIndex + 1, sourceDock.VisibleDockables.Count);
                    this.dockFactory.InsertDockable(sourceDock, newDocument, targetIndex);
                }

                if (sourceDock is IDocumentDock sourceDocDock)
                {
                    placementDock = sourceDocDock;
                }
            }
        }

        if (focus)
        {
            if (newDocument is not null)
            {
                this.dockFactory.SetActiveDockable(newDocument);
                this.dockFactory.SetFocusedDockable(placementDock, newDocument);
            }
            // Set SelectedTab directly so GoToPane notification-read works even when the
            // ItemsSource/ItemContainerGenerator pipeline is inactive (e.g. headless tests).
            targetPane.SelectedTab = tab;
            if (!this.navigatingViaHistory)
            {
                this.navigationHistoryService.Push(new NavigationEntry(tab.Id, targetPane.Id));
            }
        }
    }

    public async Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab)
    {
        // Ensure we have a real workspace loaded (not the placeholder)
        await this.EnsureWorkspaceLoadedAsync();

        var pane = this.selectedWorkspacePane;
        if (pane?.ContentLayout is null)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(pane.ContentLayout);
        if (documentDock is null)
        {
            return;
        }

        var existingDocument = this.dockFactory.GetDocumentForTab(oldTab.Id);
        if (existingDocument is null)
        {
            // Old tab doesn't exist, just open the new one
            await this.OpenTabAsync(newTab);
            return;
        }

        // Remember position and active state
        var paneIndex = pane.Tabs.IndexOf(oldTab);
        var wasActive = ReferenceEquals(documentDock.ActiveDockable, existingDocument);

        // Remove old tab from pane.Tabs; ItemsSource removes the WorkspaceDocument automatically.
        if (paneIndex >= 0)
            pane.Tabs.RemoveAt(paneIndex);
        else
            pane.Tabs.Remove(oldTab);

        // Insert new tab at the same position; ItemsSource creates a new WorkspaceDocument automatically.
        if (paneIndex >= 0 && paneIndex <= pane.Tabs.Count)
            pane.Tabs.Insert(paneIndex, newTab);
        else
            pane.Tabs.Add(newTab);

        if (wasActive)
        {
            var newDocument = this.dockFactory.GetDocumentForTab(newTab.Id);
            if (newDocument is not null)
            {
                this.dockFactory.SetActiveDockable(newDocument);
                this.dockFactory.SetFocusedDockable(documentDock, newDocument);
            }
        }

        await DisposeWorkspaceTabAsync(oldTab);
    }

    public void CloseTab(WorkspaceTabViewModel tab)
    {
        foreach (var pane in this.WorkspacePanes)
        {
            if (!pane.Tabs.Contains(tab)) continue;

            // Capture whether this tab was active before removing it
            var wasActive = false;
            if (pane.ContentLayout is not null)
            {
                var documentDock = this.FindDocumentDock(pane.ContentLayout);
                wasActive = documentDock?.ActiveDockable is WorkspaceDocument activeDoc
                    && string.Equals(activeDoc.Id, tab.Id, StringComparison.Ordinal);
            }

            // Removing from pane.Tabs removes the WorkspaceDocument via ItemsSource automatically.
            pane.Tabs.Remove(tab);

            // Navigate to MRU tab if we just closed the active tab
            if (wasActive)
            {
                this.navigatingViaHistory = true;
                try
                {
                    if (this.navigationHistoryService.GoBackSkipping(this.IsTabOpen, out var entry) && entry is not null)
                    {
                        this.ActivateTabById(entry.TabId, entry.WorkspacePaneId);
                    }
                }
                finally
                {
                    this.navigatingViaHistory = false;
                }
            }

            _ = DisposeWorkspaceTabAsync(tab);
            return;
        }
    }

    public async Task DuplicateBrowserTabAsync()
    {
        var layout = this.selectedWorkspacePane?.ContentLayout;
        if (layout is null)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(layout);
        var activeTab = (documentDock?.ActiveDockable as WorkspaceDocument)?.TabViewModel;

        if (activeTab is not WebViewModel webVm)
        {
            return;
        }

        var url = webVm.AddressBarUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var newTab = new WebViewModel(url, this)
        {
            Id = $"web-{Guid.NewGuid()}",
            Title = url,
            DockRegion = webVm.DockRegion,
        };

        await this.OpenTabAsync(newTab, insertAfterTabId: webVm.Id);
    }

    internal bool CloseTabById(string tabId)
    {
        foreach (var pane in this.WorkspacePanes)
        {
            var tab = pane.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
            if (tab is null) continue;

            // Capture whether this tab was active before removing it
            var wasActive = false;
            if (pane.ContentLayout is not null)
            {
                var documentDock = this.FindDocumentDock(pane.ContentLayout);
                wasActive = documentDock?.ActiveDockable is WorkspaceDocument activeDoc
                    && string.Equals(activeDoc.Id, tab.Id, StringComparison.Ordinal);
            }

            // Removing from pane.Tabs removes the WorkspaceDocument via ItemsSource automatically.
            pane.Tabs.Remove(tab);

            // Navigate to MRU tab if we just closed the active tab
            if (wasActive)
            {
                this.navigatingViaHistory = true;
                try
                {
                    if (this.navigationHistoryService.GoBackSkipping(this.IsTabOpen, out var entry) && entry is not null)
                    {
                        this.ActivateTabById(entry.TabId, entry.WorkspacePaneId);
                    }
                }
                finally
                {
                    this.navigatingViaHistory = false;
                }
            }

            _ = DisposeWorkspaceTabAsync(tab);
            return true;
        }

        return false;
    }

    private void SubscribeToInnerDockChanges(WorkspacePaneViewModel workspacePane)
    {
        // Fix #1107: no-op. The former dock->Tabs order back-sync has been removed; Tabs order is
        // now independent of dock order, so there is nothing to react to on VisibleDockables
        // CollectionChanged. The subscription helpers are retained as empty hooks to keep the many
        // existing call sites simple; they may be removed once no callers remain.
        _ = workspacePane;
    }

    private void UnsubscribeFromInnerDockChanges(WorkspacePaneViewModel workspacePane)
    {
        // Fix #1107: no-op. See SubscribeToInnerDockChanges above.
        _ = workspacePane;
    }

    /// <summary>
    /// Writes the current visual tab order and dock layout back to the workspace entity.
    /// Call explicitly after user-initiated tab changes (open, close).
    /// Returns the underlying update task so callers can await completion when needed.
    /// </summary>
    internal Task<UpdateResult> WriteBackWorkspaceTabs(WorkspacePaneViewModel workspacePane)
    {
        if (this.entityBroker is null) return Task.FromResult(new UpdateResult { EntityResults = Array.Empty<EntityUpdateResult>() });
        var entityData = workspacePane.Entity.Data;
        if (entityData is not JsonElement dataElement || dataElement.ValueKind != JsonValueKind.Object) return Task.FromResult(new UpdateResult { EntityResults = Array.Empty<EntityUpdateResult>() });

        // Build tabs array as workspace-tab-descriptors
        var tabDescriptors = new JsonArray();
        foreach (var tab in workspacePane.Tabs)
        {
            var descriptor = BuildTabDescriptor(tab);
            if (descriptor is not null)
            {
                tabDescriptors.Add(descriptor);
            }
        }

        // Serialize dock layout: DockState.Save captures split proportions and active-dockable
        // state into the layout tree. Owner back-references are handled by ReferenceHandler.Preserve
        // ($ref markers); WorkspaceDockTypeInfoResolver strips Type-typed properties that STJ cannot
        // serialize (e.g. Avalonia StyledElement.StyleKey).
        JsonNode? dockLayout = null;
        if (workspacePane.ContentLayout is not null)
        {
            this.dockFactory.DockState.Save(workspacePane.ContentLayout);

            var serializer = new DockSerializer(
                typeof(System.Collections.ObjectModel.ObservableCollection<>),
                new WorkspaceDockTypeInfoResolver());
            var layoutJson = serializer.Serialize(workspacePane.ContentLayout);

            if (!string.IsNullOrWhiteSpace(layoutJson))
            {
                dockLayout = JsonNode.Parse(layoutJson);
            }
        }

        // Build merged entity data with updated tabs and dock-layout
        var entityNode = JsonNode.Parse(dataElement.GetRawText())?.AsObject();
        if (entityNode is null) return Task.FromResult(new UpdateResult { EntityResults = Array.Empty<EntityUpdateResult>() });

        entityNode["tabs"] = tabDescriptors;

        // Capture the currently active tab's ID
        var documentDock = workspacePane.ContentLayout is not null
            ? this.FindDocumentDock(workspacePane.ContentLayout)
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

        // Remove legacy fields that we no longer write
        entityNode.Remove("focused-tab-id");

        var updatedJson = entityNode.ToJsonString();
        using var doc = JsonDocument.Parse(updatedJson);
        var updatedData = doc.RootElement.Clone();

        var changes = new List<EntityChange>
        {
            new()
            {
                EntityId = workspacePane.Entity.EntityId,
                ConcurrencyTag = workspacePane.Entity.ConcurrencyTag,
                Data = updatedData,
                EntityChangeMode = EntityChangeMode.Replace,
            },
        };
        AppendWorkspaceTabRelationshipChanges(workspacePane, changes);

        return this.entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "Update workspace tabs" },
            },
            Changes = changes,
        });
    }

    private Task SaveWorkspacePaneAsync(WorkspacePaneViewModel workspacePane) =>
        this.WriteBackWorkspaceTabs(workspacePane);

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

    /// <summary>
    /// Builds a workspace-tab-descriptor <see cref="JsonObject"/> for write-back.
    /// Returns null for tab types that cannot be serialized.
    /// </summary>
    private static JsonObject? BuildTabDescriptor(WorkspaceTabViewModel tab)
    {
        JsonObject? content = null;

        if (tab.Entity is { } entity)
        {
            // Entity-reference tab: write as UUID string (entity-reference = entity-id | entity-name)
            content = new JsonObject
            {
                ["target-entity-name"] = entity.EntityId.Value.ToString(),
            };
        }
        else if (tab is WebViewModel webVm && !string.IsNullOrWhiteSpace(webVm.AddressBarUrl))
        {
            // Browser-URL tab
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

    // Fix #1107: SyncPaneTabsFromDockChange, SyncPaneTabsOrderFromDock, RunSuppressingDockOrderSync
    // and the suppressDockOrderSync field have been deleted. Tab order (WorkspacePaneViewModel.Tabs)
    // is now a pure, order-independent membership set — only explicit open/close code mutates it —
    // and the dock's VisibleDockables is the sole source of visual/dock order. This removes the
    // reentrant back-sync that crashed ObservableCollection with
    // "Cannot change ObservableCollection during a CollectionChanged event" when opening a tab
    // after a guarded insert.

    private IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = this.FindDocumentDock(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Recursively enumerates every <see cref="WorkspaceDocument"/> in a dock layout tree.
    /// Walks the entire <see cref="IDock.VisibleDockables"/> hierarchy so that documents
    /// inside split panes are discovered, not just those in the primary content dock.
    /// </summary>
    internal static IEnumerable<WorkspaceDocument> EnumerateAllDocuments(IDockable dockable)
    {
        if (dockable is WorkspaceDocument doc)
        {
            yield return doc;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                foreach (var found in EnumerateAllDocuments(child))
                {
                    yield return found;
                }
            }
        }
    }

    internal string? FindWorkspacePaneIdForTab(string tabId)
    {
        foreach (var pane in this.WorkspacePanes)
        {
            if (pane.Tabs.Any(t => string.Equals(t.Id, tabId, StringComparison.Ordinal)))
                return pane.Id;
        }
        return null;
    }

    private static async Task DisposeWorkspaceTabAsync(
        WorkspaceTabViewModel workspaceTab)
    {
        await workspaceTab.DisposeAsync();
    }

    public void OnDockableTabClosed(WorkspaceTabViewModel tabVm)
    {
        foreach (var pane in WorkspacePanes)
        {
            if (pane.Tabs.Contains(tabVm))
            {
                // Capture whether this tab was active before removing it
                var wasActive = false;
                if (pane.ContentLayout is not null)
                {
                    var documentDock = this.FindDocumentDock(pane.ContentLayout);
                    wasActive = documentDock?.ActiveDockable is WorkspaceDocument activeDoc
                        && string.Equals(activeDoc.Id, tabVm.Id, StringComparison.Ordinal);
                }

                pane.Tabs.Remove(tabVm);

                // Navigate to MRU tab if we just closed the active tab
                if (wasActive)
                {
                    this.navigatingViaHistory = true;
                    try
                    {
                        if (this.navigationHistoryService.GoBackSkipping(this.IsTabOpen, out var entry) && entry is not null)
                        {
                            this.ActivateTabById(entry.TabId, entry.WorkspacePaneId);
                        }
                    }
                    finally
                    {
                        this.navigatingViaHistory = false;
                    }
                }

                break;
            }
        }
        _ = DisposeWorkspaceTabAsync(tabVm);
    }

    private async Task OpenEntityBrowserTabAsync()
    {
        const string entityBrowserTabId = "entity-browser-tab";

        await this.EnsureWorkspaceLoadedAsync();
        if (this.selectedWorkspacePane?.ContentLayout is { } layout)
        {
            var documentDock = this.FindDocumentDock(layout);
            var existingDocument = documentDock?.VisibleDockables
                ?.OfType<WorkspaceDocument>()
                .FirstOrDefault(d => string.Equals(d.Id, entityBrowserTabId, StringComparison.Ordinal));
            if (existingDocument is not null)
            {
                this.dockFactory.SetActiveDockable(existingDocument);
                this.dockFactory.SetFocusedDockable(documentDock!, existingDocument);
                return;
            }
        }

        var subscribedGet = await this.EntityBroker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                    },
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            });

        var entityBrowserTab = new EntityBrowserWorkspaceTabViewModel(this.EntityBroker, subscribedGet)
        {
            Id = entityBrowserTabId,
            Title = "Entity Browser",
            DockRegion = "full",
        };

        await this.OpenTabAsync(entityBrowserTab);
    }

    private async Task<EntitySnapshot?> LoadSingleEntitySnapshotAsync(
        GetEntityRequest request)
    {
        var entities = await this.EntityBroker.GetEntitiesAsync([request]);
        return entities.FirstOrDefault()?.Snapshot;
    }

    private IReadOnlyCollection<GetEntityRequest> BuildWorkspaceEntityRequests(
        JsonElement workspaceData)
    {
        var requests = new List<GetEntityRequest>();
        var seenNames = new HashSet<EntityName>();

        // Collect tab declarations from the new tabs[] property first, then fall back to legacy regions[].
        var tabDeclarations = CollectTabDeclarations(workspaceData);
        foreach (var tab in tabDeclarations)
        {
            if (tab.ValueKind != JsonValueKind.Object
                || !tab.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("target-entity-name", out var targetEntityName))
            {
                continue;
            }

            var entityName = ReadEntityName(targetEntityName);
            if (entityName is null || !seenNames.Add(entityName.Value))
            {
                continue;
            }

            requests.Add(new GetEntityRequest
            {
                EntityName = entityName,
            });
        }

        return requests;
    }

    /// <summary>
    /// Collects tab declarations from workspaceData, preferring the new <c>tabs</c> array
    /// and falling back to flattening the legacy <c>regions[].tabs</c>.
    /// </summary>
    private static IReadOnlyList<JsonElement> CollectTabDeclarations(JsonElement workspaceData)
    {
        // Prefer new top-level tabs array
        if (workspaceData.TryGetProperty("tabs", out var tabsElement)
            && tabsElement.ValueKind == JsonValueKind.Array)
        {
            return tabsElement.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.Object)
                .ToList();
        }

        // Legacy: flatten regions[].tabs
        if (!workspaceData.TryGetProperty("regions", out var regions)
            || regions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<JsonElement>();
        foreach (var region in regions.EnumerateArray())
        {
            if (region.ValueKind != JsonValueKind.Object
                || !region.TryGetProperty("tabs", out var tabs)
                || tabs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var tab in tabs.EnumerateArray())
            {
                if (tab.ValueKind == JsonValueKind.Object)
                {
                    result.Add(tab);
                }
            }
        }
        return result;
    }

    private async Task PopulateWorkspacePaneTabsAsync(
        WorkspacePaneViewModel workspacePane,
        SubscribedEntityViewModel workspaceEntity,
        JsonElement workspaceData)
    {
        // Try to restore from dock-layout first (preserves split positions and tab descriptors)
        if (workspaceData.TryGetProperty("dock-layout", out var dockLayoutElement)
            && dockLayoutElement.ValueKind == JsonValueKind.Object)
        {
            var dockLayoutJson = dockLayoutElement.GetRawText();
            if (await this.TryRestoreFromDockLayoutAsync(workspacePane, workspaceEntity, dockLayoutJson))
            {
                return;
            }
        }

        // Collect tab declarations: prefer new tabs[] array, fall back to legacy regions[].tabs
        var tabDeclarations = CollectTabDeclarations(workspaceData);

        // Determine the active/focused tab id before any async work so it is available inside InvokeAsync.
        // Prefer new active-tab-id property, falling back to legacy focused-tab-id.
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

        // Load all tabs in parallel, preserving declaration order
        var tabAdded = false;
        if (tabDeclarations.Count > 0)
        {
            var tabResults = await Task.WhenAll(
                tabDeclarations.Select(tabDecl => this.TryFetchWorkspaceTabAsync(tabDecl)));

            // Add to pane.Tabs on the UI thread; WorkspaceDocumentGenerator creates dock documents automatically.
            // Activate the saved active tab in the same dispatcher frame: PrepareDocumentContainer fires
            // synchronously during Tabs.Add, so GetDocumentForTab is reliable immediately after Add.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var workspaceClosed = false;
                foreach (var workspaceTab in tabResults)
                {
                    if (workspaceTab is null) continue;

                    // Guard: workspace may have been closed while tabs were loading
                    if (workspaceClosed || !this.WorkspacePanes.Contains(workspacePane))
                    {
                        workspaceClosed = true;
                        _ = DisposeWorkspaceTabAsync(workspaceTab);
                        continue;
                    }

                    workspacePane.Tabs.Add(workspaceTab);
                    tabAdded = true;
                }

                if (tabAdded && !workspaceClosed && !string.IsNullOrEmpty(activeTabId))
                {
                    var focusedDoc = this.dockFactory.GetDocumentForTab(activeTabId);
                    if (focusedDoc is not null)
                    {
                        this.dockFactory.SetActiveDockable(focusedDoc);
                    }
                }
            });
        }

        if (!tabAdded)
        {
            // Fall back to a default entity view for the workspace itself
            var defaultTab = new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog, this)
            {
                Id = workspaceEntity.EntityId.ToString(),
                Title = workspaceEntity.DisplayName,
                Entity = workspaceEntity,
                DockRegion = "full",
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!this.WorkspacePanes.Contains(workspacePane))
                {
                    _ = DisposeWorkspaceTabAsync(defaultTab);
                    return;
                }

                workspacePane.Tabs.Add(defaultTab);
            });
        }
    }

    /// <summary>
    /// Attempts to restore workspace tabs from the saved dock-layout JSON.
    /// Deserializes the layout structure (preserving split positions), recreates tab VMs
    /// from <see cref="DockTabDescriptor"/> nodes, and wires them into the content dock.
    /// Returns true when at least one tab was successfully restored.
    /// </summary>
    private async Task<bool> TryRestoreFromDockLayoutAsync(
        WorkspacePaneViewModel workspacePane,
        SubscribedEntityViewModel workspaceEntity,
        string dockLayoutJson)
    {
        IRootDock? layout;
        try
        {
            var serializer = new DockSerializer(
                typeof(System.Collections.ObjectModel.ObservableCollection<>),
                new WorkspaceDockTypeInfoResolver());
            layout = serializer.Deserialize<IRootDock>(dockLayoutJson);
        }
        catch
        {
            return false;
        }

        if (layout is null) return false;

        // Walk the entire layout tree to find all stub documents (handles split layouts)
        var stubs = EnumerateAllDocuments(layout)
            .Where(d => d.Descriptor is not null)
            .ToList();

        if (stubs.Count == 0) return false;

        // Create tab VMs from descriptors in parallel
        var tabVmTasks = stubs.Select(stub =>
            this.CreateTabViewModelFromDescriptorAsync(workspaceEntity, stub.Descriptor!, stub.Id));
        var tabResults = await Task.WhenAll(tabVmTasks);

        bool success = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!this.WorkspacePanes.Contains(workspacePane)) return;

            // Populate ContextLocator for every stub before calling InitLayout so
            // base.InitDockable wires each stub's Context from the locator.
            this.dockFactory.ContextLocator ??= new Dictionary<string, Func<object?>>();
            for (int i = 0; i < stubs.Count; i++)
            {
                var tabVm = tabResults[i];
                if (tabVm is null) continue;
                var stubId = stubs[i].Id;
                this.dockFactory.ContextLocator[stubId] = () => tabVm;
            }

            // Switch to the restored layout and wire Owner/Factory/Context for every node
            this.UnsubscribeFromInnerDockChanges(workspacePane);
            workspacePane.ContentLayout = layout;
            this.dockFactory.InitLayout(layout);
            this.dockFactory.DockState.Restore(layout);
            this.SubscribeToInnerDockChanges(workspacePane);

            // Find the primary ContentDock and configure it for future dynamic tab management.
            // The split structure (sibling ContentDocks in a ProportionalDock) is preserved
            // because we only replace the primary dock's VisibleDockables.
            var contentDock = this.FindDocumentDock(layout) as WorkspaceContentDock;
            if (contentDock is not null)
            {
                contentDock.VisibleDockables?.Clear();
                contentDock.ItemsSource = workspacePane.Tabs;
                contentDock.ItemContainerGenerator = new WorkspaceDocumentGenerator(
                    doc => this.dockFactory.RegisterDocument(doc.Id, doc),
                    id => this.dockFactory.UnregisterDocument(id));
            }

            // Add tab VMs; the generator creates dock documents in the primary content dock
            bool workspaceClosed = false;
            for (int i = 0; i < stubs.Count; i++)
            {
                var tabVm = tabResults[i];
                if (tabVm is null) continue;

                if (workspaceClosed || !this.WorkspacePanes.Contains(workspacePane))
                {
                    workspaceClosed = true;
                    _ = DisposeWorkspaceTabAsync(tabVm);
                    continue;
                }

                workspacePane.Tabs.Add(tabVm);
                success = true;
            }
        });

        return success;
    }

    /// <summary>
    /// Creates a <see cref="WorkspaceTabViewModel"/> from a <see cref="DockTabDescriptor"/>
    /// by fetching the referenced entity (if any) and constructing the appropriate tab type.
    /// </summary>
    private async Task<WorkspaceTabViewModel?> CreateTabViewModelFromDescriptorAsync(
        SubscribedEntityViewModel workspaceEntity,
        DockTabDescriptor descriptor,
        string tabId)
    {
        switch (descriptor)
        {
            case AgentSessionDockTabDescriptor agentDesc:
                if (Guid.TryParse(agentDesc.EntityId, out var agentGuid))
                {
                    var entityId = new EntityId(agentGuid);
                    var entities = await this.EntityBroker!.GetEntitiesAsync(
                        [new GetEntityRequest { EntityId = entityId }]);
                    var entity = entities.FirstOrDefault();
                    if (entity is not null && this.openAgentSessionShortcutHandler is not null)
                    {
                        var agentTab = await this.openAgentSessionShortcutHandler
                            .TryCreateAgentSessionTabForRestoreAsync(
                                this, entity, tabId, title: null, dockRegion: null);
                        if (agentTab is not null) return agentTab;
                    }
                }
                break;

            case EntityDockTabDescriptor entityDesc:
                if (Guid.TryParse(entityDesc.EntityId, out var entityGuid))
                {
                    var entityId = new EntityId(entityGuid);
                    var entities = await this.EntityBroker!.GetEntitiesAsync(
                        [new GetEntityRequest { EntityId = entityId }]);
                    var entity = entities.FirstOrDefault();
                    if (entity is not null)
                    {
                        return new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog, this)
                        {
                            Id = tabId,
                            Title = entity.DisplayName,
                            Entity = entity,
                            DockRegion = "full",
                        };
                    }
                }
                break;

            case BrowserDockTabDescriptor browserDesc:
                if (!string.IsNullOrWhiteSpace(browserDesc.Url))
                {
                    return new WebViewModel(browserDesc.Url)
                    {
                        Id = tabId,
                        Title = browserDesc.Url,
                        DockRegion = "full",
                    };
                }
                break;
        }

        return null;
    }

    private async Task<WorkspaceTabViewModel?> TryFetchWorkspaceTabAsync(JsonElement tab)
    {
        if (!tab.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Resolve entity from target-entity-name (async, handles both entity-id and entity-name refs)
        var reference = content.TryReadEntityReference("target-entity-name");
        if (reference.HasValue)
        {
            GetEntityRequest request;
            if (reference.Value.EntityName is { } entityName)
            {
                request = new GetEntityRequest { EntityName = entityName };
            }
            else if (reference.Value.EntityId is { } entityId)
            {
                request = new GetEntityRequest { EntityId = entityId };
            }
            else
            {
                return null;
            }

            var fetched = await this.EntityBroker!.GetEntitiesAsync([request]);
            var targetEntity = fetched.FirstOrDefault();
            if (targetEntity is not null)
            {
                return await this.CreateTabFromEntityAsync(tab, content, targetEntity);
            }
        }

        // Fall back to URL-based tab
        if (content.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            return new WebViewModel(url.GetString()!)
            {
                Id = ReadString(tab, "tab-id") ?? url.GetString()!,
                Title = ReadString(tab, "title") ?? url.GetString()!,
                DockRegion = ReadString(tab, "dock") ?? "full",
            };
        }

        return null;
    }

    private async Task<WorkspaceTabViewModel?> CreateTabFromEntityAsync(
        JsonElement tab,
        JsonElement content,
        SubscribedEntityViewModel targetEntity)
    {
        var tabId = ReadString(tab, "tab-id");
        var title = ReadString(tab, "title");
        var dockRegion = ReadString(tab, "dock");

        // #1129: Route the workspace-open/restore path through the Open shortcut pipeline
        // so any interactive entity type (external browser, agent-session, shell, and any
        // future handler) reconstitutes into its correct tab kind — not the generic entity
        // card. The pipeline preserves the persisted tab-id/title/dock so the restored dock
        // layout remains stable. Falls back to the generic EntityWorkspaceTabViewModel for
        // non-interactive entities (no handler claims Open, or the handler declines).
        var restoredTab = await this.shortcutManager.TryCreateTabForRestoreAsync(
            this, Shortcut.Open, targetEntity, tabId, title, dockRegion);
        if (restoredTab is not null)
        {
            return restoredTab;
        }

        // Default: generic entity view
        return new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog, this)
        {
            Id = tabId ?? targetEntity.EntityId.ToString(),
            Title = title ?? targetEntity.DisplayName,
            Entity = targetEntity,
            DockRegion = dockRegion ?? "full",
        };
    }

    private async Task<WorkspacePaneViewModel> CreateWorkspacePaneAsync(
        SubscribedEntityViewModel workspaceEntity,
        JsonElement workspaceData)
    {
        var workspacePane = new WorkspacePaneViewModel(workspaceEntity, null, this.CloseWorkspaceCommand, this.SaveWorkspacePaneAsync);
        
        // Create this workspace's own dock layout for its content tabs
        workspacePane.ContentLayout = this.dockFactory.CreateWorkspaceContentLayout(workspacePane);
        this.SubscribeToInnerDockChanges(workspacePane);
        
        var tabs = new List<WorkspaceTabViewModel>();

        if (workspaceData.TryGetProperty("regions", out var workspaceRegions)
            && workspaceRegions.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in workspaceRegions.EnumerateArray())
            {
                if (region.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Extract all tabs from this region
                if (region.TryGetProperty("tabs", out var regionTabs)
                    && regionTabs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tab in regionTabs.EnumerateArray())
                    {
                        if (tab.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var workspaceTab = await this.TryReadWorkspaceTabContentAsync(tab);
                        if (workspaceTab is not null)
                        {
                            tabs.Add(workspaceTab);
                        }
                    }
                }
            }
        }

        // If no tabs found, create a default tab for the workspace entity
        if (tabs.Count == 0)
        {
            tabs.Add(new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog, this)
            {
                Id = workspaceEntity.EntityId.ToString(),
                Title = workspaceEntity.DisplayName,
                Entity = workspaceEntity,
                DockRegion = "full",
            });
        }

        // Add all tabs to this workspace's ContentLayout and to pane.Tabs
        // ItemsSource wiring means pane.Tabs.Add() automatically creates the WorkspaceDocument.
        if (workspacePane.ContentLayout is not null)
        {
            foreach (var tab in tabs)
            {
                workspacePane.Tabs.Add(tab);
            }
        }

        return workspacePane;
    }

    private async Task<WorkspaceTabViewModel?> TryReadWorkspaceTabContentAsync(
        JsonElement tab)
    {
        if (!tab.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Try entity reference through the broker
        if (this.entityBroker?.TryGetReferencedEntity(content, "target-entity-name", out var targetEntity) == true
            && targetEntity is not null)
        {
            var tabId = ReadString(tab, "tab-id");
            var title = ReadString(tab, "title");
            var dockRegion = ReadString(tab, "dock");

            // #1129: Route the workspace-open/restore path through the Open shortcut
            // pipeline so any interactive entity type (external browser, agent-session,
            // shell, and any future handler) reconstitutes into its correct tab kind — not
            // the generic entity card. Preserves the persisted tab-id / title / dock so the
            // restored dock layout remains stable. Falls through to the generic entity
            // card only when no interactive handler claims the entity.
            var restoredTab = await this.shortcutManager.TryCreateTabForRestoreAsync(
                this, Shortcut.Open, targetEntity, tabId, title, dockRegion);
            if (restoredTab is not null)
            {
                return restoredTab;
            }

            // Default entity view
            return new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog, this)
            {
                Id = tabId ?? targetEntity.EntityId.ToString(),
                Title = title ?? targetEntity.DisplayName,
                Entity = targetEntity,
                DockRegion = dockRegion ?? "full",
            };
        }

        if (content.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            return new WebViewModel(url.GetString()!)
            {
                Id = ReadString(tab, "tab-id") ?? url.GetString()!,
                Title = ReadString(tab, "title") ?? url.GetString()!,
                DockRegion = ReadString(tab, "dock") ?? "full",
            };
        }

        return null;
    }

    private async Task OpenStartupWorkspaceAsync()
    {
        var defaultWorkspaceIds = await this.QueryDefaultWorkspaceIdsAsync();
        if (defaultWorkspaceIds.Count > 0)
        {
            foreach (var workspaceId in defaultWorkspaceIds)
            {
                await this.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
            }

            return;
        }

        await this.OpenWorkspaceAsync(
            new GetEntityRequest
            {
                EntityName = new EntityName("workspaces", "getting-started-workspace"),
            });
    }

    private async Task<SubscribedEntityViewModel?> LoadNavigationSubscriptionAsync()
    {
        var mainViewRequest = new GetEntityRequest
        {
            EntityName = new EntityName("views", "main"),
        };

        var mainViewEntities = await this.EntityBroker.GetEntitiesAsync([mainViewRequest]);
        var mainViewEntity = mainViewEntities.FirstOrDefault();
        if (mainViewEntity?.Snapshot.Data is not JsonElement mainViewData)
        {
            return null;
        }

        var requests = new List<GetEntityRequest>
        {
            mainViewRequest,
        };

        if (mainViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (TryReadEntityRequest(subView, "view-entity-id", out var request))
                {
                    requests.Add(request);
                }
            }
        }

        await this.EntityBroker!.GetEntitiesAsync(requests);
        return mainViewEntity;
    }

    private async Task EnsureWorkspaceLoadedAsync()
    {
        if (!string.Equals(this.SelectedWorkspacePane.Id, DefaultWorkspaceId, StringComparison.Ordinal))
            return;

        if (this.configuration?.SkipStartupWorkspace == true)
        {
            if (this.SelectedWorkspacePane.ContentLayout is null)
            {
                this.SelectedWorkspacePane.ContentLayout = this.dockFactory.CreateWorkspaceContentLayout(this.SelectedWorkspacePane);
                this.SubscribeToInnerDockChanges(this.SelectedWorkspacePane);
            }
            return;
        }

        await this.OpenGettingStartedWorkspaceAsync();
    }

    private async Task OpenGettingStartedWorkspaceAsync()
    {
        var defaultWorkspaceIds = await this.QueryDefaultWorkspaceIdsAsync();
        if (defaultWorkspaceIds.Count > 0)
        {
            foreach (var workspaceId in defaultWorkspaceIds)
            {
                await this.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
            }

            return;
        }

        await this.OpenWorkspaceAsync(
            new GetEntityRequest
            {
                EntityName = new EntityName("workspaces", "getting-started-workspace"),
            });
    }

    private async Task<IReadOnlyList<EntityId>> QueryDefaultWorkspaceIdsAsync()
    {
        if (this.entityBroker is not { } broker)
        {
            return [];
        }

        var profileId = broker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        if (profileId == default)
        {
            return [];
        }

        var queryResult = await broker.EntityRepository.DataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("default-workspaces"),
                        Clause = new AndQueryClause
                        {
                            Clauses =
                            [
                                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["default"]) },
                                new EntityFieldQueryClause
                                {
                                    FieldPath = new FieldPath("participants", "applied-to"),
                                    ComparisonOperator = FieldComparisonOperator.Equals,
                                    Value = JsonSerializer.SerializeToElement(profileId.Value.ToString()),
                                },
                            ],
                        },
                    },
                ],
            });

        var workspaceIds = new List<EntityId>();
        foreach (var snapshot in queryResult.Batches.SelectMany(static batch => batch.Entities))
        {
            if (snapshot.Data is not { } data
                || !data.TryGetProperty("participants", out var participants)
                || !participants.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            var reference = valueElement.TryReadEntityReference();
            if (reference?.EntityId is { } entityId)
            {
                workspaceIds.Add(entityId);
            }
        }

        return workspaceIds;
    }


    private static string? ReadString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string? ReadPrimaryName(
        JsonElement element)
    {
        if (!element.TryGetProperty("names", out var names)
            || names.ValueKind != JsonValueKind.Array
            || names.GetArrayLength() == 0)
        {
            return null;
        }

        var first = names[0];
        if (first.ValueKind == JsonValueKind.String)
        {
            return first.GetString();
        }

        if (first.ValueKind == JsonValueKind.Array)
        {
            var parts = first.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join("/", parts!);
        }

        return null;
    }

    private static EntityName? ReadEntityName(
        JsonElement property)
    {
        return property.TryReadEntityName();
    }

    private static bool TryReadPrimaryEntityName(
        JsonElement element,
        out EntityName entityName)
    {
        entityName = default;
        if (!element.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array
            || namesElement.GetArrayLength() == 0)
        {
            return false;
        }

        var firstName = namesElement[0].TryReadEntityName();
        if (firstName is null)
        {
            return false;
        }

        entityName = firstName.Value;
        return true;
    }

    private static string GetWorkspaceRequestDisplayText(
        GetEntityRequest request)
    {
        if (request.EntityName is EntityName entityName)
        {
            return string.Join("/", entityName.Components);
        }

        if (request.EntityId is EntityId entityId)
        {
            return entityId.ToString();
        }

        return "workspace";
    }

    private static string GetWorkspaceRequestKey(
        GetEntityRequest request)
    {
        if (request.EntityName is EntityName entityName)
        {
            return JsonSerializer.Serialize(entityName.Components);
        }

        if (request.EntityId is EntityId entityId)
        {
            return entityId.ToString();
        }

        return "unknown";
    }

    private static bool TryReadSubViewGetRequest(
        JsonElement subView,
        out GetRequest getRequest)
    {
        getRequest = null!;
        if (!subView.TryGetProperty("get-entity", out var getEntitiesElement)
            || getEntitiesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var getEntities = new List<GetEntityRequest>();
        foreach (var getEntityElement in getEntitiesElement.EnumerateArray())
        {
            if (TryReadGetEntityRequest(getEntityElement, out var getEntityRequest))
            {
                getEntities.Add(getEntityRequest);
            }
        }

        if (getEntities.Count == 0)
        {
            return false;
        }

        getRequest = new GetRequest
        {
            Entities = getEntities,
            RelationshipsToReturn = TryReadGetRelationshipRequests(subView, "relationships-to-return", out var relationshipsToReturn)
                ? relationshipsToReturn
                : null,
            Timestamps = TryReadTimestamps(subView, "timestamps", out var timestamps)
                ? timestamps
                : [null],
        };
        return true;
    }

    private static bool TryReadSubViewQueryRequest(
        JsonElement subView,
        out QueryRequest queryRequest)
    {
        queryRequest = null!;
        if (!subView.TryGetProperty("query", out var queryElement)
            || queryElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var deserialized = queryElement.Deserialize<QueryRequest>(WebDataAccessJsonSerialization.Options);
        if (deserialized is null || deserialized.Clauses.Count == 0)
        {
            return false;
        }

        queryRequest = deserialized with
        {
            RelationshipsToReturn = TryReadGetRelationshipRequests(subView, "relationships-to-return", out var relationshipsToReturn)
                ? relationshipsToReturn
                : null,
        };
        return true;
    }

    private static bool TryReadGetEntityRequest(
        JsonElement getEntityElement,
        out GetEntityRequest getEntityRequest)
    {
        getEntityRequest = null!;
        if (getEntityElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        EntityId? entityId = null;
        if (getEntityElement.TryGetProperty("entity-id", out var entityIdElement)
            && entityIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(entityIdElement.GetString(), out var parsedEntityId))
        {
            entityId = new EntityId(parsedEntityId);
        }

        EntityName? entityName = null;
        if (getEntityElement.TryGetProperty("entity-name", out var entityNameElement))
        {
            entityName = entityNameElement.TryReadEntityName();
        }

        EntityTypeNameSet? entityTypeNameSet = null;
        if (TryReadStringArray(getEntityElement, "entity-type-names", out var entityTypeNames))
        {
            entityTypeNameSet = new EntityTypeNameSet(entityTypeNames!);
        }

        if (entityId is null && entityName is null && entityTypeNameSet is null)
        {
            return false;
        }

        var enumerateChildren = EnumerateChildrenAction.EnumerateSelf;
        if (getEntityElement.TryGetProperty("enumerate-children", out var enumerateChildrenElement)
            && enumerateChildrenElement.ValueKind == JsonValueKind.String
            && !TryReadEnumerateChildrenAction(enumerateChildrenElement.GetString(), out enumerateChildren))
        {
            return false;
        }

        getEntityRequest = new GetEntityRequest
        {
            EntityId = entityId,
            EntityName = entityName,
            EnumerateChildren = enumerateChildren,
            EntityTypeNames = entityTypeNameSet,
            RelationshipsToReturn = TryReadGetRelationshipRequests(getEntityElement, "relationships-to-return", out var relationshipsToReturn)
                ? relationshipsToReturn
                : null,
        };
        return true;
    }

    private static bool TryReadGetRelationshipRequests(
        JsonElement parentElement,
        string propertyName,
        out IReadOnlyCollection<GetRelationshipRequest> getRelationshipRequests)
    {
        getRelationshipRequests = Array.Empty<GetRelationshipRequest>();
        if (!parentElement.TryGetProperty(propertyName, out var relationshipsElement)
            || relationshipsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<GetRelationshipRequest>();
        foreach (var relationshipElement in relationshipsElement.EnumerateArray())
        {
            if (relationshipElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            RelationshipTypeNameSet? relationshipTypeNames = null;
            if (TryReadStringArray(relationshipElement, "relationship-type-names", out var relationshipTypeNameValues))
            {
                relationshipTypeNames = new RelationshipTypeNameSet(relationshipTypeNameValues!);
            }

            RoleNameSet? relationshipRoleNames = null;
            if (TryReadStringArray(relationshipElement, "relationship-role-names", out var relationshipRoleNameValues))
            {
                relationshipRoleNames = new RoleNameSet(relationshipRoleNameValues!);
            }

            parsed.Add(
                new GetRelationshipRequest
                {
                    RelationshipTypeNames = relationshipTypeNames,
                    RelationshipRoleNames = relationshipRoleNames,
                });
        }

        getRelationshipRequests = parsed;
        return true;
    }

    private static bool TryReadStringArray(
        JsonElement parentElement,
        string propertyName,
        out string[]? values)
    {
        values = null;
        if (!parentElement.TryGetProperty(propertyName, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsedValues = arrayElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        if (parsedValues.Length == 0)
        {
            return false;
        }

        values = parsedValues!;
        return true;
    }

    private static bool TryReadEnumerateChildrenAction(
        string? value,
        out EnumerateChildrenAction enumerateChildrenAction)
    {
        enumerateChildrenAction = EnumerateChildrenAction.EnumerateSelf;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("self", StringComparison.Ordinal))
        {
            enumerateChildrenAction = EnumerateChildrenAction.EnumerateSelf;
            return true;
        }

        if (value.Equals("children", StringComparison.Ordinal))
        {
            enumerateChildrenAction = EnumerateChildrenAction.EnumerateChildren;
            return true;
        }

        if (value.Equals("all-children", StringComparison.Ordinal))
        {
            enumerateChildrenAction = EnumerateChildrenAction.EnumerateAllChildren;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out enumerateChildrenAction);
    }

    private static bool TryReadTimestamps(
        JsonElement parentElement,
        string propertyName,
        out IReadOnlyCollection<Timestamp?> timestamps)
    {
        timestamps = Array.Empty<Timestamp?>();
        if (!parentElement.TryGetProperty(propertyName, out var timestampsElement)
            || timestampsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsedTimestamps = new List<Timestamp?>();
        foreach (var timestampElement in timestampsElement.EnumerateArray())
        {
            if (timestampElement.ValueKind == JsonValueKind.Null)
            {
                parsedTimestamps.Add(null);
                continue;
            }

            if (timestampElement.ValueKind != JsonValueKind.Object
                || !timestampElement.TryGetProperty("datetime", out var dateTimeElement)
                || dateTimeElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(dateTimeElement.GetString(), out var dateTimeOffset)
                || !timestampElement.TryGetProperty("change-id", out var changeIdElement)
                || changeIdElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(changeIdElement.GetString()))
            {
                continue;
            }

            parsedTimestamps.Add(new Timestamp(dateTimeOffset, changeIdElement.GetString()!));
        }

        timestamps = parsedTimestamps;
        return true;
    }

    private static bool TryReadEntityRequest(
        JsonElement parent,
        string propertyName,
        out GetEntityRequest request)
    {
        request = null!;
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (Guid.TryParse(text, out _))
            {
                request = new GetEntityRequest
                {
                    EntityId = new EntityId(text),
                };
                return true;
            }
        }

        var entityName = ReadEntityName(property);
        if (entityName is null)
        {
            return false;
        }

        request = new GetEntityRequest
        {
            EntityName = entityName,
        };
        return true;
    }

    private static string? ReadLocalString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            var locale = CultureInfo.CurrentUICulture.Name;
            if (property.TryGetProperty(locale, out var localizedValue)
                && localizedValue.ValueKind == JsonValueKind.String)
            {
                return localizedValue.GetString();
            }

            var neutralLocale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (property.TryGetProperty(neutralLocale, out localizedValue)
                && localizedValue.ValueKind == JsonValueKind.String)
            {
                return localizedValue.GetString();
            }

            if (property.TryGetProperty("default", out var defaultValue)
                && defaultValue.ValueKind == JsonValueKind.String)
            {
                return defaultValue.GetString();
            }
        }

        return null;
    }

    private static double? ReadDouble(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        return null;
    }

    private void OnNotificationsChanged(object? sender, EventArgs e)
    {
        var notifications = this.notificationService.Notifications;
        foreach (var pane in this.WorkspacePanes)
        {
            var anyUnread = false;
            foreach (var tab in pane.Tabs)
            {
                var doc = this.dockFactory.GetDocumentForTab(tab.Id);
                if (doc is null) continue;
                var hasUnread = notifications.Any(n => n.TabKey == doc.Id && !n.IsRead);
                doc.HasUnreadNotification = hasUnread;
                if (hasUnread) anyUnread = true;
            }
            pane.AnyTabHasUnreadNotification = anyUnread;
        }
    }

    private void OnActiveDockableChanged(object? sender, global::Dock.Model.Core.Events.ActiveDockableChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.ActiveAgentViewModel));
        if (e.Dockable is WorkspacePaneDocument paneDoc)
        {
            this.SelectedWorkspacePane = paneDoc.WorkspacePane;
            if (!this.navigatingViaHistory)
            {
                var activeTabId = this.ActiveTabId;
                if (activeTabId is not null)
                {
                    this.navigationHistoryService.Push(new NavigationEntry(activeTabId, paneDoc.WorkspacePane.Id));
                }
            }
        }
        else if (e.Dockable is WorkspaceDocument doc)
        {
            this.notificationService.MarkRead(doc.Id);
            // Update the selected tab on the pane that owns this document
            var ownerPane = this.WorkspacePanes.FirstOrDefault(
                p => p.Tabs.Any(t => string.Equals(t.Id, doc.Id, StringComparison.Ordinal)));
            if (ownerPane is not null)
            {
                ownerPane.SelectedTab = doc.TabViewModel;
            }
            if (doc.TabViewModel is { } focusTab)
            {
                Dispatcher.UIThread.Post(
                    () => focusTab.RequestFocusPrimaryControl(),
                    Avalonia.Threading.DispatcherPriority.Input);
            }
        }
    }

    private void OnNavigateNotification(int direction)
    {
        var notifications = this.notificationService.Notifications;
        var candidates = notifications
            .Where(n => !n.IsRead)
            .OrderByDescending(n => n.When)
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = notifications
                .OrderByDescending(n => n.When)
                .ToList();
        }
        if (candidates.Count == 0) return;

        var activeId = this.ActiveTabId;
        var currentIndex = candidates.FindIndex(n => n.TabKey == activeId);
        int nextIndex;
        if (currentIndex < 0)
        {
            nextIndex = direction > 0 ? 0 : candidates.Count - 1;
        }
        else
        {
            nextIndex = ((currentIndex + direction) % candidates.Count + candidates.Count) % candidates.Count;
        }

        var target = candidates[nextIndex];
        this.notificationService.MarkRead(target.TabKey);
        this.NavigateToNotificationTab(target.TabKey);
        this.notificationsViewModel?.OpenWithHighlight(target.TabKey);
    }

    private void NavigateToNotificationTab(string tabId)
    {
        var workspacePaneId = this.notificationService.Notifications
            .FirstOrDefault(e => e.TabKey == tabId)
            ?.TabDescriptor.WorkspaceId;
        this.ActivateTabById(tabId, workspacePaneId);
        if (!this.navigatingViaHistory)
        {
            var paneId = workspacePaneId ?? this.SelectedWorkspacePane?.Id;
            if (paneId is not null)
            {
                this.navigationHistoryService.Push(new NavigationEntry(tabId, paneId));
            }
        }
    }

    public void WireWindowFocus(Action focusWindow)
    {
        if (this.notificationsViewModel is not null)
        {
            this.notificationsViewModel.FocusWindowCallback = focusWindow;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        this.refreshTimer.Stop();
        this.notificationService.NotificationsChanged -= this.OnNotificationsChanged;
        this.dockFactory.ActiveDockableChanged -= this.OnActiveDockableChanged;
        this.notificationsViewModel?.Dispose();

        await this.currentPopulation.DisposeAsync();

        if (this.scheduledToolRunner is not null)
        {
            await this.scheduledToolRunner.DisposeAsync();
        }

        this.scheduledToolsPause?.Dispose();
        this.scheduledToolsRunning?.Dispose();
        this.runningAgentBrain?.Dispose();
        this.usageTracker?.Dispose();

        if (this.usageMetricsService is not null)
        {
            await this.usageMetricsService.DisposeAsync();
        }

        if (this.devTunnelHostService is not null)
        {
            await this.devTunnelHostService.DisposeAsync();
        }

        if (this.webHost is not null)
        {
            await this.webHost.DisposeAsync();
        }

        if (this.transportComposition is not null)
        {
            await this.transportComposition.DisposeAsync();
        }

        foreach (var pane in this.WorkspacePanes)
        {
            foreach (var tab in pane.Tabs.ToArray())
            {
                await DisposeWorkspaceTabAsync(tab);
            }
        }

        this.ConnectionStatus?.Dispose();
        this.interestCatalog?.Dispose();
        this.entityTypeCatalog?.Dispose();

        foreach (var lease in this.autoResumeLeases)
        {
            await lease.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}


