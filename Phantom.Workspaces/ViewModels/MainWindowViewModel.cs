using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Notifications;

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
    private readonly List<SubscribedGet> selectedViewSubViewSubscriptions = [];
    private readonly List<SubscribedQuery> selectedViewSubViewQuerySubscriptions = [];
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
    private readonly WorkspaceDockFactory dockFactory;
    private IRootDock? layout;
    private ScheduledTools.ScheduledToolHost? scheduledToolHost;
    private ScheduledTools.ScheduledToolPauseStateService? scheduledToolPauseStateService;
    private ScheduledTools.ScheduledToolRunner? scheduledToolRunner;
    private ScheduledToolsPauseIndicatorViewModel? scheduledToolsPause;
    private readonly NotificationService notificationService;
    private NotificationsViewModel? notificationsViewModel;

    public MainWindowViewModel(
        RepositorySource repositorySource,
        WorkspacesConfiguration? configuration = null)
    {
        this.RepositorySource = repositorySource;
        this.configuration = configuration;
        this.entityBrokerTask = EntityBroker.CreateInitializedAsync(
            repositorySource,
            userComputerProfileOverride: configuration?.UserComputerProfileOverride);
        this.profileStore = ProfileStore.ForCurrentUser();

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
        this.GoToTabAtIndexCommand = new RelayCommand(param => this.OnGoToTabAtIndex(int.Parse((string)param!)));
        this.GoToWorkspacePaneAtIndexCommand = new RelayCommand(param => this.OnGoToWorkspacePaneAtIndex(int.Parse((string)param!)));
        this.ApplyThemeResources(this.currentProfile.Theme);
        this.ApplyThemeVariant(this.currentProfile.Theme.Name);
        var agentSessionShortcutContext = new AgentSessionShortcutContext(
            userComputerProfileOverride: configuration?.UserComputerProfileOverride);
        this.openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(agentSessionShortcutContext);
        this.shortcutManager.AddShortcutHandler(new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(this.openAgentSessionShortcutHandler);
        this.shortcutManager.AddShortcutHandler(new StartAgentSessionOnProfileShortcutHandler(agentSessionShortcutContext, this.openAgentSessionShortcutHandler));
        this.shortcutManager.AddShortcutHandler(new StartShellOnProfileShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new OpenExternalEntityShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new OpenEntityShortcutHandler());
        this.shortcutManager.AddShortcutHandler(new DeleteEntityShortcutHandler());

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
            tabId => _ = this.NavigateToNotificationTabAsync(tabId));
        this.NavigateNextNotificationCommand = new RelayCommand(_ => this.OnNavigateNotification(+1));
        this.NavigatePreviousNotificationCommand = new RelayCommand(_ => this.OnNavigateNotification(-1));
        this.notificationService.NotificationsChanged += this.OnNotificationsChanged;
    }

    public RepositorySource RepositorySource { get; }

    public ObservableCollection<ViewDefinitionViewModel> TopLevelViews { get; }

    public ObservableCollection<WorkspacePaneViewModel> WorkspacePanes { get; }

    public RelayCommand ActivateShortcutCommand { get; }

    public RelayCommand ActivateEntityClickCommand { get; }

    public RelayCommand SetDebuggingCommand { get; }

    public RelayCommand CloseWorkspaceCommand { get; }

    public RelayCommand CloseActiveTabCommand { get; }

    public RelayCommand CycleTabForwardCommand { get; }

    public RelayCommand CycleTabBackwardCommand { get; }

    public RelayCommand GoToTabAtIndexCommand { get; }

    public RelayCommand GoToWorkspacePaneAtIndexCommand { get; }

    public RelayCommand NavigateNextNotificationCommand { get; }
    public RelayCommand NavigatePreviousNotificationCommand { get; }

    public NotificationsViewModel? NotificationsViewModel
    {
        get => this.notificationsViewModel;
        private set => this.SetProperty(ref this.notificationsViewModel, value);
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

    public INotificationService NotificationService => this.notificationService;

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

            _ = this.SetThemeAsync(normalizedThemeName);
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

            _ = this.ApplySelectedViewAsync();
        }
    }

    public WorkspacePaneViewModel SelectedWorkspacePane
    {
        get => this.selectedWorkspacePane;
        set => this.SetProperty(ref this.selectedWorkspacePane, value);
    }

    public string RepositoryStatusText => this.RepositorySource switch
    {
        WebRepositorySource web => $"Web DAL source: {web.Endpoint}",
        LocalGitRepositorySource git => $"Local git source: {git.Path}",
        MongoDbRepositorySource mongo => $"MongoDb DAL source: {mongo.ContainerName}/{mongo.RootCollectionName}",
        _ => "In-memory repository source.",
    };

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

    /// <summary>
    /// Reflects the persisted scheduled-tools pause state on the clock / scheduled-tools button, and
    /// toggles it. Null until <see cref="InitializeAsync"/> has composed the scheduled-tools runtime.
    /// </summary>
    public ScheduledToolsPauseIndicatorViewModel? ScheduledToolsPause
    {
        get => this.scheduledToolsPause;
        private set => this.SetProperty(ref this.scheduledToolsPause, value);
    }

    /// <summary>
    /// Creates the scheduled tasks view model (scheduled tool-relationships plus the tool-execution
    /// results tree), or returns null if the workspace has not finished initializing.
    /// </summary>
    internal ScheduledTasksViewModel? TryCreateScheduledTasksViewModel()
        => this.entityBroker is { } broker
            ? new ScheduledTasksViewModel(broker, this.scheduledToolPauseStateService, this.HostProfileEntityId)
            : null;

    private EntityId HostProfileEntityId =>
        this.entityBroker?.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId
        ?? default;

    public async Task InitializeAsync()
    {
        this.entityBroker = await this.entityBrokerTask;
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
        await this.OpenStartupWorkspaceAsync();
        this.refreshTimer.Start();
        await this.InitializeWebHostAsync();
        await this.InitializeScheduledToolsAsync();
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
            new Tools.GitWorkspaceScanTool(),
            new Tools.CopilotSessionDiscoveryTool(),
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

        this.scheduledToolRunner = ScheduledTools.ScheduledToolRunner.Create(
            this.scheduledToolHost,
            hostEntityId,
            hostNameComponents,
            pollInterval: TimeSpan.FromMinutes(1));
        this.scheduledToolRunner.Start();
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

    private async Task InitializeWebHostAsync()
    {
        if (this.configuration is null)
        {
            return;
        }

        var reverseExecutionRegistry = new Llm.Trust.ReverseExecutionRegistry();
        this.webHost = new WorkspacesWebHost(reverseExecutionRegistry);
        this.ConnectionStatus = new ConnectionStatusViewModel(
            reverseExecutionRegistry,
            action => Dispatcher.UIThread.Post(action));

        if (this.configuration.RemoteHosting.Enabled && this.entityBroker is not null)
        {
            await this.webHost.StartAsync(
                this.configuration.RemoteHosting,
                this.entityBroker.EntityRepository.DataAccessLayer);
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
        var hostService = new Services.DevTunnel.DevTunnelServiceFactory().CreateHostService();
        this.devTunnelHostService = hostService;
        hostService.StatusChanged += (_, status) => Dispatcher.UIThread.Post(
            () => this.ConnectionStatus?.SetDevTunnelStatus(status.State, status.AccessPointUrl, status.LastError));

        // Hosting runs in the background and surfaces progress/errors through the status event, so a
        // sign-in or relay failure never blocks GUI startup. The task is observed to avoid an
        // unobserved-exception escalation; the Error status already carries the failure detail.
        this.devTunnelHostStartTask = ObserveAsync(
            hostService.StartAsync(localPort, devTunnelConfiguration));

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

    private async void OnWorkspacesDockCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Handle removed workspace documents
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is WorkspacePaneDocument workspacePaneDoc)
                {
                    await this.RemoveWorkspacePaneAsync(workspacePaneDoc.WorkspacePane);
                }
            }
        }
    }

    private async Task InitializeProfileAsync()
    {
        var profile = await this.profileStore.GetOrInitializeProfileAsync();
        this.ApplyProfile(profile);
    }

    private async Task SetThemeAsync(
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
        Application.Current!.RequestedThemeVariant = string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    private void ApplyThemeResources(
        ProfileThemeSettings theme)
    {
        var resources = Application.Current!.Resources;
        SetResource(resources, "Theme.FontFamily", new FontFamily(theme.Fonts.BaseFamily));
        SetResource(resources, "Theme.FontSize.Base", theme.Fonts.BaseSize * theme.Fonts.GlobalScale.Value);

        SetBrushResource(resources, "Theme.Surface.EntityPane.Background", theme.Surfaces.EntityPane.Background);
        SetBrushResource(resources, "Theme.Surface.EntityPane.Border", theme.Surfaces.EntityPane.Border);
        SetBrushResource(resources, "Theme.Surface.EntityPane.HoverBackground", theme.Surfaces.EntityPane.HoverBackground);
        SetBrushResource(resources, "Theme.Surface.EntityPane.HoverBorder", theme.Surfaces.EntityPane.HoverBorder);
        SetBrushResource(resources, "Theme.Surface.EntityPane.SelectedBackground", theme.Surfaces.EntityPane.SelectedBackground);
        SetBrushResource(resources, "Theme.Surface.EntityPane.SelectedBorder", theme.Surfaces.EntityPane.SelectedBorder);

        SetBrushResource(resources, "Theme.Surface.EntityCard.Background", theme.Surfaces.EntityCard.Background);
        SetBrushResource(resources, "Theme.Surface.EntityCard.Border", theme.Surfaces.EntityCard.Border);
        SetBrushResource(resources, "Theme.Surface.EntityCard.HoverBackground", theme.Surfaces.EntityCard.HoverBackground);
        SetBrushResource(resources, "Theme.Surface.EntityCard.HoverBorder", theme.Surfaces.EntityCard.HoverBorder);
        SetBrushResource(resources, "Theme.Surface.EntityCard.SelectedBackground", theme.Surfaces.EntityCard.SelectedBackground);
        SetBrushResource(resources, "Theme.Surface.EntityCard.SelectedBorder", theme.Surfaces.EntityCard.SelectedBorder);

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
        SetBrushResource(resources, $"Theme.Class.{className}.Foreground", themeClass.Foreground);
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
        var selectedView = this.selectedTopLevelView ?? EmptyView;
        this.selectedViewSubViewSubscriptions.Clear();
        this.selectedViewSubViewQuerySubscriptions.Clear();
        selectedView.Entities.Clear();
        if (string.Equals(selectedView.Id, EmptyView.Id, StringComparison.Ordinal))
        {
            this.StickyParentContextText = string.Empty;
            return;
        }

        if (selectedView.IsEntityBrowser)
        {
            await this.OpenEntityBrowserTabAsync();
            this.StickyParentContextText = string.Empty;
            return;
        }

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

        var associatedNoteEntity = await this.LoadAssociatedViewNoteAsync(selectedViewData);
        if (associatedNoteEntity is not null)
        {
            selectedView.Entities.Add(this.CreateViewEntityViewModel(associatedNoteEntity, indentLevel: 0, isParentContext: true));
        }

        await this.LoadSubViewEntitiesAsync(selectedViewData);

        if (selectedViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (this.EntityBroker.TryGetReferencedEntity(subView, "view-entity-id", out var subViewEntity)
                    && subViewEntity is not null)
                {
                    selectedView.Entities.Add(this.CreateViewEntityViewModel(subViewEntity, indentLevel: 0));
                    continue;
                }

                if (TryReadSubViewGetRequest(subView, out var getRequest))
                {
                    var getEntities = await this.LoadGetSubViewEntitiesAsync(getRequest);
                    await this.AddSubViewEntitiesWithHierarchyAsync(selectedView, getEntities);
                    continue;
                }

                if (TryReadSubViewQueryRequest(subView, out var queryRequest))
                {
                    var queryEntities = await this.LoadQuerySubViewEntitiesAsync(queryRequest);
                    await this.AddSubViewEntitiesWithHierarchyAsync(selectedView, queryEntities);
                }
            }
        }

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
        GetRequest getRequest)
    {
        var subscribedGet = await this.EntityBroker.SubscribeGetAsync(getRequest);
        this.selectedViewSubViewSubscriptions.Add(subscribedGet);

        if (subscribedGet.Results.Count == 0)
        {
            return Array.Empty<SubscribedEntityViewModel>();
        }

        return subscribedGet.Results.ToArray();
    }

    private async Task<IReadOnlyList<SubscribedEntityViewModel>> LoadQuerySubViewEntitiesAsync(
        QueryRequest queryRequest)
    {
        // Exclude not-interesting targets at the query level (via a join) unless the user opts to show
        // hidden items.
        var effectiveQuery = this.ShowHiddenItems
            ? queryRequest
            : NotInterestingQuery.ExcludingNotInteresting(queryRequest);

        // Also fetch each matched entity's interest relationships so its badge glyphs can be rendered.
        if (this.interestCatalog is { InterestTypeNames.Count: > 0 } catalog)
        {
            effectiveQuery = effectiveQuery with
            {
                RelationshipsToReturn =
                [
                    new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet([.. catalog.InterestTypeNames]) },
                ],
            };
        }

        var subscribedQuery = await this.EntityBroker.SubscribeQueryAsync(effectiveQuery);
        this.selectedViewSubViewQuerySubscriptions.Add(subscribedQuery);

        if (subscribedQuery.Results.Count == 0)
        {
            return Array.Empty<SubscribedEntityViewModel>();
        }

        return subscribedQuery.Results.ToArray();
    }

    private async Task<SubscribedEntityViewModel?> LoadAssociatedViewNoteAsync(
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
        this.selectedViewSubViewSubscriptions.Add(noteSubscription);
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
    private ViewEntityViewModel CreateViewEntityViewModel(
        SubscribedEntityViewModel entity,
        int indentLevel,
        bool isParentContext = false)
    {
        // Project the entity's interests (from its loaded relationships) into toggleable badge glyphs.
        if (this.interestCatalog is { } interestCatalog && this.entityTypeCatalog is { } entityTypeCatalog)
        {
            entity.Badges.SetBadges(InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity.Snapshot));
        }

        // Project the entity's annotated status fields into colored status badges. Discovery is
        // asynchronous (each field's status annotation is resolved through the schema), so the badges
        // arrive after the card is created and populate the entity's observable status-badge model.
        _ = this.PopulateStatusBadgesAsync(entity);

        return new ViewEntityViewModel(
            entity,
            this,
            this.shortcutManager,
            indentLevel,
            isParentContext,
            this.fieldEditorFactory);
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
        ViewDefinitionViewModel selectedView,
        IReadOnlyList<SubscribedEntityViewModel> rootEntities)
    {
        var hierarchy = await new ViewHierarchyAssembler(this.EntityBroker).AssembleAsync(rootEntities);
        foreach (var node in hierarchy)
        {
            this.AddHierarchyNode(selectedView, node, indentLevel: 0);
        }
    }

    private void AddHierarchyNode(
        ViewDefinitionViewModel selectedView,
        ViewHierarchyNode node,
        int indentLevel)
    {
        selectedView.Entities.Add(this.CreateViewEntityViewModel(node.Entity, indentLevel));
        foreach (var child in node.Children)
        {
            this.AddHierarchyNode(selectedView, child, indentLevel + 1);
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
        if (this.mainNavigationView is not null
            && e.ChangedEntityIds.Contains(this.mainNavigationView.EntityId))
        {
            this.InitializeTopLevelViews();
        }

        _ = this.ApplySelectedViewAsync();
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
            return;
        }

        // Phase 1: create skeleton workspace pane and show it immediately
        var workspacePane = new WorkspacePaneViewModel(workspaceEntity, null, this.CloseWorkspaceCommand);
        workspacePane.ContentLayout = this.dockFactory.CreateWorkspaceContentLayout(workspacePane);

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
        _ = this.PopulateWorkspacePaneTabsAsync(workspacePane, workspaceEntity, workspaceData);
    }

    private void AddWorkspacePaneToDock(WorkspacePaneViewModel workspacePane)
    {
        if (this.Layout is null)
        {
            return;
        }

        var workspacesDock = FindDocumentDock(this.Layout);
        if (workspacesDock is not null)
        {
            // Check if already in dock
            var existingDocument = workspacesDock.VisibleDockables
                ?.OfType<WorkspacePaneDocument>()
                .FirstOrDefault(doc => doc.WorkspacePane == workspacePane);
            
            if (existingDocument is null)
            {
                this.dockFactory.AddWorkspacePane(workspacesDock, workspacePane);
            }
            else
            {
                // Already in dock, just activate it
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

    private async void OnCloseWorkspace(object? parameter)
    {
       if (parameter is not WorkspacePaneViewModel pane)
       {
           return;
       }

       await this.RemoveWorkspacePaneAsync(pane);
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

        this.dockFactory.CloseDockable(activeDoc);
        DisposeWorkspaceTab(activeDoc.TabViewModel);
    }

    private void OnCycleTab(int delta)
    {
        if (this.selectedWorkspacePane?.ContentLayout is null)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(this.selectedWorkspacePane.ContentLayout);
        var dockables = documentDock?.VisibleDockables;
        if (documentDock is null || dockables is null || dockables.Count < 2)
        {
            return;
        }

        var currentIndex = documentDock.ActiveDockable is { } active
            ? dockables.IndexOf(active)
            : 0;

        var nextIndex = ((currentIndex + delta) % dockables.Count + dockables.Count) % dockables.Count;
        var nextDockable = dockables[nextIndex];
        this.dockFactory.SetActiveDockable(nextDockable);
        this.dockFactory.SetFocusedDockable(documentDock, nextDockable);
        if (nextDockable is WorkspaceDocument cycledDoc)
        {
            this.notificationService.MarkRead(cycledDoc.Id);
        }
    }

    private void OnGoToTabAtIndex(int index)
    {
        if (this.selectedWorkspacePane?.ContentLayout is null)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(this.selectedWorkspacePane.ContentLayout);
        if (documentDock?.VisibleDockables is not { } tabs || index >= tabs.Count)
        {
            return;
        }

        var target = tabs[index];
        this.dockFactory.SetActiveDockable(target);
        this.dockFactory.SetFocusedDockable(documentDock, target);
        if (target is WorkspaceDocument doc)
        {
            this.notificationService.MarkRead(doc.Id);
        }
    }

    private void OnGoToWorkspacePaneAtIndex(int index)
    {
        if (index >= this.WorkspacePanes.Count)
        {
            return;
        }

        this.SelectedWorkspacePane = this.WorkspacePanes[index];
    }

    internal async Task RemoveWorkspacePaneAsync(WorkspacePaneViewModel pane)
    {
       // Don't allow closing the default placeholder
       if (string.Equals(pane.Id, DefaultWorkspaceId, StringComparison.Ordinal))
       {
           return;
       }

       var paneIndex = this.WorkspacePanes.IndexOf(pane);
       if (paneIndex < 0)
       {
           return;
       }

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
        GetEntityRequest entityRequest)
    {
        var entities = await this.EntityBroker!.GetEntitiesAsync([entityRequest]);
        var subscribedEntity = entities.FirstOrDefault();
        if (subscribedEntity is null)
        {
            return;
        }

        await this.OpenTabAsync(
            new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog)
            {
                Id = subscribedEntity.EntityId.ToString(),
                Title = subscribedEntity.DisplayName,
                Entity = subscribedEntity,
            });
    }

    public async Task OpenTabAsync(WorkspaceTabViewModel tab)
    {
        // Ensure we have a real workspace loaded (not the placeholder)
        await this.EnsureWorkspaceLoadedAsync();
        
        if (this.selectedWorkspacePane?.ContentLayout is null)
        {
            return;
        }

        // Find the document dock in the selected workspace's ContentLayout
        var documentDock = this.FindDocumentDock(this.selectedWorkspacePane.ContentLayout);
        if (documentDock is null)
        {
            return;
        }

        // Check if tab already exists
        var existingDocument = documentDock.VisibleDockables
            ?.OfType<WorkspaceDocument>()
            .FirstOrDefault(doc => string.Equals(doc.Id, tab.Id, StringComparison.Ordinal));

        if (existingDocument is not null)
        {
            // Already exists, just activate it
            if (!ReferenceEquals(existingDocument.TabViewModel, tab))
            {
                DisposeWorkspaceTab(tab);
            }
            this.dockFactory.SetActiveDockable(existingDocument);
            this.notificationService.MarkRead(tab.Id);
            this.dockFactory.SetFocusedDockable(documentDock, existingDocument);
            this.SyncSelectedWorkspacePaneFromDock();
            return;
        }

        // Create new document
        this.dockFactory.AddWorkspaceTab(documentDock, tab);
        this.SyncSelectedWorkspacePaneFromDock();
    }

    public async Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab)
    {
        // Ensure we have a real workspace loaded (not the placeholder)
        await this.EnsureWorkspaceLoadedAsync();
        
        if (this.selectedWorkspacePane?.ContentLayout is null)
        {
            return;
        }

        // Find the document dock in the selected workspace's ContentLayout
        var documentDock = this.FindDocumentDock(this.selectedWorkspacePane.ContentLayout);
        if (documentDock is null)
        {
            return;
        }

        // Find the existing document
        var visibleDockables = documentDock.VisibleDockables;
        if (visibleDockables is null)
        {
            // No visible dockables, just open the new tab
            await this.OpenTabAsync(newTab);
            return;
        }

        var existingDocument = visibleDockables
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(doc => string.Equals(doc.Id, oldTab.Id, StringComparison.Ordinal));

        if (existingDocument is null)
        {
            // Old tab doesn't exist, just open the new one
            await this.OpenTabAsync(newTab);
            return;
        }

        // Remember position and active state
        var documentIndex = visibleDockables.IndexOf(existingDocument);
        var wasActive = ReferenceEquals(documentDock.ActiveDockable, existingDocument);
        
        // Remove the old document
        visibleDockables.Remove(existingDocument);

        // Create new document with the new tab
        var newDocument = new WorkspaceDocument(newTab);
        
        // Insert at the same position
        if (documentIndex >= 0 && documentIndex < visibleDockables.Count)
        {
            visibleDockables.Insert(documentIndex, newDocument);
        }
        else
        {
            visibleDockables.Add(newDocument);
        }

        // Set as active if it was before
        if (wasActive)
        {
            this.dockFactory?.SetActiveDockable(newDocument);
            this.dockFactory?.SetFocusedDockable(documentDock, newDocument);
        }
        
        // Dispose the old tab
        DisposeWorkspaceTab(oldTab);
    }

    public void CloseTab(WorkspaceTabViewModel tab)
    {
        foreach (var pane in this.WorkspacePanes)
        {
            if (pane.ContentLayout is null)
            {
                continue;
            }

            var documentDock = this.FindDocumentDock(pane.ContentLayout);
            if (documentDock?.VisibleDockables is null)
            {
                continue;
            }

            var document = documentDock.VisibleDockables
                .OfType<WorkspaceDocument>()
                .FirstOrDefault(doc => string.Equals(doc.Id, tab.Id, StringComparison.Ordinal));

            if (document is null)
            {
                continue;
            }

            this.dockFactory.CloseDockable(document);
            DisposeWorkspaceTab(document.TabViewModel);
            return;
        }
    }

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

    private void SyncSelectedWorkspacePaneFromDock()
    {
        if (this.selectedWorkspacePane is not null)
        {
            this.SyncWorkspacePaneFromDock(this.selectedWorkspacePane);
        }
    }

    private void SyncWorkspacePaneFromDock(WorkspacePaneViewModel workspacePane)
    {
        if (workspacePane.ContentLayout is null)
        {
            return;
        }

        var documentDock = this.FindDocumentDock(workspacePane.ContentLayout);
        if (documentDock is null || documentDock.VisibleDockables is null)
        {
            return;
        }

        // Find the active document
        var activeDocument = documentDock.ActiveDockable as WorkspaceDocument
            ?? documentDock.VisibleDockables.OfType<WorkspaceDocument>().FirstOrDefault();

        if (activeDocument is null)
        {
            return;
        }

        // Create a synthetic region view for backward compatibility with tests
        var region = new WorkspaceRegionViewModel
        {
            Id = "center",
            Title = "Center",
            DockRegion = "center",
            RelativeSize = 1.0,
        };

        // Populate the region with all documents
        region.Tabs.Clear();
        foreach (var doc in documentDock.VisibleDockables.OfType<WorkspaceDocument>())
        {
            region.Tabs.Add(doc.TabViewModel);
        }

        region.SelectedTab = activeDocument.TabViewModel;

        // Update the workspace pane
        workspacePane.Regions.Clear();
        workspacePane.Regions.Add(region);
        workspacePane.SelectedRegion = region;
    }

    private static void DisposeWorkspaceTab(
        WorkspaceTabViewModel workspaceTab)
    {
        switch (workspaceTab)
        {
            case IAsyncDisposable asyncDisposable:
                _ = asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private async Task OpenEntityBrowserTabAsync()
    {
        const string entityBrowserTabId = "entity-browser-tab";
        
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

        if (!workspaceData.TryGetProperty("regions", out var regions)
            || regions.ValueKind != JsonValueKind.Array)
        {
            return requests;
        }

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
        }

        return requests;
    }

    private async Task PopulateWorkspacePaneTabsAsync(
        WorkspacePaneViewModel workspacePane,
        SubscribedEntityViewModel workspaceEntity,
        JsonElement workspaceData)
    {
        var contentDock = this.FindDocumentDock(workspacePane.ContentLayout!);
        if (contentDock is null)
        {
            return;
        }

        // Collect all tab declarations from regions
        var tabDeclarations = new List<JsonElement>();
        if (workspaceData.TryGetProperty("regions", out var regions)
            && regions.ValueKind == JsonValueKind.Array)
        {
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
                        tabDeclarations.Add(tab);
                    }
                }
            }
        }

        // Load each tab asynchronously in parallel; add each to the dock as it resolves
        var tabAdded = false;
        if (tabDeclarations.Count > 0)
        {
            var tabTasks = tabDeclarations.Select(async tabDecl =>
            {
                var workspaceTab = await this.TryFetchWorkspaceTabAsync(tabDecl);
                if (workspaceTab is null)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Guard: workspace may have been closed while tabs were loading
                    if (!this.WorkspacePanes.Contains(workspacePane))
                    {
                        DisposeWorkspaceTab(workspaceTab);
                        return;
                    }

                    this.dockFactory.AddWorkspaceTab(contentDock, workspaceTab);
                    tabAdded = true;
                });
            }).ToList();

            await Task.WhenAll(tabTasks);
        }

        if (!tabAdded)
        {
            // Fall back to a default entity view for the workspace itself
            var defaultTab = new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog)
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
                    DisposeWorkspaceTab(defaultTab);
                    return;
                }

                this.dockFactory.AddWorkspaceTab(contentDock, defaultTab);
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() => this.SyncWorkspacePaneFromDock(workspacePane));
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
            return new BrowserWorkspaceTabViewModel
            {
                Id = ReadString(tab, "tab-id") ?? url.GetString()!,
                Title = ReadString(tab, "title") ?? url.GetString()!,
                Url = url.GetString()!,
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
        // External entity → embedded browser tab
        if (targetEntity.IsEntityType("external"))
        {
            var urls = OpenExternalEntityShortcutHandler.ParseUrls(targetEntity);
            if (urls.Count > 0)
            {
                var entityUrl = urls.ContainsKey("default") ? urls["default"] : urls.First().Value;
                return new WebViewModel(entityUrl, this)
                {
                    Id = ReadString(tab, "tab-id") ?? $"web-{targetEntity.EntityId}",
                    Title = ReadString(tab, "title") ?? targetEntity.DisplayName,
                    DockRegion = ReadString(tab, "dock") ?? "full",
                };
            }
        }

        // Agent-session entity → restore via dedicated handler
        if (targetEntity.IsEntityType("agent-session") && this.openAgentSessionShortcutHandler is not null)
        {
            var agentSessionTab = await this.openAgentSessionShortcutHandler
                .TryCreateAgentSessionTabForRestoreAsync(
                    this,
                    targetEntity,
                    tabId: ReadString(tab, "tab-id"),
                    title: ReadString(tab, "title"),
                    dockRegion: ReadString(tab, "dock"));
            if (agentSessionTab is not null)
            {
                return agentSessionTab;
            }
        }

        // Default: generic entity view
        return new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog)
        {
            Id = ReadString(tab, "tab-id") ?? targetEntity.EntityId.ToString(),
            Title = ReadString(tab, "title") ?? targetEntity.DisplayName,
            Entity = targetEntity,
            DockRegion = ReadString(tab, "dock") ?? "full",
        };
    }

    private async Task<WorkspacePaneViewModel> CreateWorkspacePaneAsync(
        SubscribedEntityViewModel workspaceEntity,
        JsonElement workspaceData)
    {
        var workspacePane = new WorkspacePaneViewModel(workspaceEntity, null, this.CloseWorkspaceCommand);
        
        // Create this workspace's own dock layout for its content tabs
        workspacePane.ContentLayout = this.dockFactory.CreateWorkspaceContentLayout(workspacePane);
        
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
            tabs.Add(new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog)
            {
                Id = workspaceEntity.EntityId.ToString(),
                Title = workspaceEntity.DisplayName,
                Entity = workspaceEntity,
                DockRegion = "full",
            });
        }

        // Add all tabs to this workspace's ContentLayout
        var contentDock = FindDocumentDock(workspacePane.ContentLayout);
        if (contentDock is not null)
        {
            foreach (var tab in tabs)
            {
                this.dockFactory.AddWorkspaceTab(contentDock, tab);
            }
        }

        // Sync the workspace pane with its ContentLayout for backward compatibility
        this.SyncWorkspacePaneFromDock(workspacePane);

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
            // For external entities, create WebViewModel with embedded browser
            if (targetEntity.IsEntityType("external"))
            {
                var urls = OpenExternalEntityShortcutHandler.ParseUrls(targetEntity);
                if (urls.Count > 0)
                {
                    // Use the first URL (or "default" URL if available)
                    var entityUrl = urls.ContainsKey("default") ? urls["default"] : urls.First().Value;

                    return new WebViewModel(entityUrl, this)
                    {
                        Id = ReadString(tab, "tab-id") ?? $"web-{targetEntity.EntityId}",
                        Title = ReadString(tab, "title") ?? targetEntity.DisplayName,
                        DockRegion = ReadString(tab, "dock") ?? "full",
                    };
                }
            }

            // For agent-session entities, restore through the dedicated handler so the agent is
            // correctly initialised (same path as a manual open via OpenAgentSessionShortcutHandler).
            if (targetEntity.IsEntityType("agent-session") && this.openAgentSessionShortcutHandler is not null)
            {
                var agentSessionTab = await this.openAgentSessionShortcutHandler
                    .TryCreateAgentSessionTabForRestoreAsync(
                        this,
                        targetEntity,
                        tabId: ReadString(tab, "tab-id"),
                        title: ReadString(tab, "title"),
                        dockRegion: ReadString(tab, "dock"));
                if (agentSessionTab is not null)
                {
                    return agentSessionTab;
                }
                // Fall through to generic entity view if creation fails (missing data etc.).
            }

            // Default entity view
            return new EntityWorkspaceTabViewModel(this.EntityBroker, this.entityTypeViewCatalog)
            {
                Id = ReadString(tab, "tab-id") ?? targetEntity.EntityId.ToString(),
                Title = ReadString(tab, "title") ?? targetEntity.DisplayName,
                Entity = targetEntity,
                DockRegion = ReadString(tab, "dock") ?? "full",
            };
        }

        if (content.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            return new BrowserWorkspaceTabViewModel
            {
                Id = ReadString(tab, "tab-id") ?? url.GetString()!,
                Title = ReadString(tab, "title") ?? url.GetString()!,
                Url = url.GetString()!,
                DockRegion = ReadString(tab, "dock") ?? "full",
            };
        }

        return null;
    }

    private async Task OpenStartupWorkspaceAsync()
    {
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

    private WorkspaceRegionViewModel GetOrCreateSelectedWorkspaceRegion()
    {
        if (this.SelectedWorkspacePane.SelectedRegion is not null)
        {
            return this.SelectedWorkspacePane.SelectedRegion;
        }

        if (this.SelectedWorkspacePane.Regions.Count == 0)
        {
            var region = new WorkspaceRegionViewModel
            {
                Id = "center",
                Title = "Center",
                DockRegion = "center",
                RelativeSize = 1,
            };
            this.SelectedWorkspacePane.Regions.Add(region);
            this.SelectedWorkspacePane.SelectedRegion = region;
            return region;
        }

        this.SelectedWorkspacePane.SelectedRegion = this.SelectedWorkspacePane.Regions[0];
        return this.SelectedWorkspacePane.SelectedRegion;
    }

    private async Task EnsureWorkspaceLoadedAsync()
    {
        // If the current workspace is the placeholder, open the getting-started workspace
        if (string.Equals(this.SelectedWorkspacePane.Id, DefaultWorkspaceId, StringComparison.Ordinal))
        {
            await this.OpenGettingStartedWorkspaceAsync();
        }
    }

    private async Task OpenGettingStartedWorkspaceAsync()
    {
        await this.OpenWorkspaceAsync(
            new GetEntityRequest
            {
                EntityName = new EntityName("workspaces", "getting-started-workspace"),
            });
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

        queryRequest = deserialized;
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
            if (pane.ContentLayout is null) continue;
            var documentDock = this.FindDocumentDock(pane.ContentLayout);
            if (documentDock?.VisibleDockables is null) continue;
            foreach (var dockable in documentDock.VisibleDockables.OfType<WorkspaceDocument>())
            {
                var hasUnread = notifications.Any(n => n.TabKey == dockable.Id && !n.IsRead);
                dockable.HasUnreadNotification = hasUnread;
            }
        }
    }

    private void OnNavigateNotification(int direction)
    {
        var notifications = this.notificationService.Notifications;
        var candidates = notifications
            .Where(n => !n.IsRead)
            .OrderByDescending(n => n.Timestamp)
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = notifications
                .OrderByDescending(n => n.Timestamp)
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
        _ = this.NavigateToNotificationTabAsync(target.TabKey);
    }

    private async Task NavigateToNotificationTabAsync(string tabId)
    {
        foreach (var pane in this.WorkspacePanes)
        {
            if (pane.ContentLayout is null) continue;
            var documentDock = this.FindDocumentDock(pane.ContentLayout);
            if (documentDock?.VisibleDockables is null) continue;
            var doc = documentDock.VisibleDockables
                .OfType<WorkspaceDocument>()
                .FirstOrDefault(d => d.Id == tabId);
            if (doc is not null)
            {
                this.dockFactory.SetActiveDockable(doc);
                this.dockFactory.SetFocusedDockable(documentDock, doc);
                return;
            }
        }
        // Tab not found open - nothing to do (reopen not implemented in this iteration)
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        this.notificationService.NotificationsChanged -= this.OnNotificationsChanged;
        this.notificationsViewModel?.Dispose();

        if (this.scheduledToolRunner is not null)
        {
            await this.scheduledToolRunner.DisposeAsync();
        }

        this.scheduledToolsPause?.Dispose();

        if (this.devTunnelHostService is not null)
        {
            await this.devTunnelHostService.DisposeAsync();
        }

        if (this.webHost is not null)
        {
            await this.webHost.DisposeAsync();
        }

        this.ConnectionStatus?.Dispose();
        this.interestCatalog?.Dispose();
        this.entityTypeCatalog?.Dispose();
    }
}

