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
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
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
    private EntityBroker? entityBroker;
    private SubscribedEntityViewModel? mainNavigationView;
    private readonly ProfileStore profileStore;
    private readonly DispatcherTimer refreshTimer;
    private ViewDefinitionViewModel selectedTopLevelView = EmptyView;
    private WorkspacePaneViewModel selectedWorkspacePane;
    private string stickyParentContextText = string.Empty;
    private Profile currentProfile = Profile.Default;
    private string selectedThemeName = ProfileThemeSettings.Dark.Name;
    private bool suppressThemeSelectionChange;

    public MainWindowViewModel(
        RepositorySource repositorySource)
    {
        this.RepositorySource = repositorySource;
        this.entityBrokerTask = EntityBroker.CreateInitializedAsync(repositorySource);
        this.profileStore = ProfileStore.ForCurrentUser();

        this.TopLevelViews = new ObservableCollection<ViewDefinitionViewModel>();
        this.WorkspacePanes = new ObservableCollection<WorkspacePaneViewModel>
        {
            CreatePlaceholderWorkspacePane(DefaultWorkspaceId, "No workspace selected."),
        };

        this.selectedWorkspacePane = this.WorkspacePanes[0];
        this.ActivateEntityCommand = new RelayCommand(async _ => await this.OnActivateEntityAsync(_));
        this.SetDebuggingCommand = new RelayCommand(async parameter => await this.SetDebuggingAsync(ReadDebuggingParameter(parameter)));
        this.ApplyThemeResources(this.currentProfile.Theme);
        this.ApplyThemeVariant(this.currentProfile.Theme.Name);

        this.refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        this.refreshTimer.Tick += this.OnRefreshTick;
    }

    public RepositorySource RepositorySource { get; }

    public ObservableCollection<ViewDefinitionViewModel> TopLevelViews { get; }

    public ObservableCollection<WorkspacePaneViewModel> WorkspacePanes { get; }

    public RelayCommand ActivateEntityCommand { get; }

    public RelayCommand SetDebuggingCommand { get; }

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

    public string RepositoryStatusText => this.RepositorySource.SourceType switch
    {
        RepositorySourceType.Web => $"Web DAL source: {this.RepositorySource.RawValue}",
        RepositorySourceType.LocalGit => $"Local git source: {this.RepositorySource.RawValue}",
        RepositorySourceType.MongoDb => $"MongoDb DAL source: {this.RepositorySource.MongoDbContainerName}/{this.RepositorySource.MongoDbRootCollectionName}",
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

    private EntityBroker EntityBroker => this.entityBroker
        ?? throw new InvalidOperationException("The view model has not been initialized.");

    public async Task InitializeAsync()
    {
        this.entityBroker = await this.entityBrokerTask;
        this.entityBroker.Changed += this.OnEntityBrokerChanged;
        this.mainNavigationView = await this.LoadNavigationSubscriptionAsync();
        this.InitializeTopLevelViews();
        await this.ApplySelectedViewAsync();
        await this.InitializeProfileAsync();
        await this.OpenStartupWorkspaceAsync();
        this.refreshTimer.Start();
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

    private static WorkspacePaneViewModel CreatePlaceholderWorkspacePane(
        string paneId,
        string displayName)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["workspace"],
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
        return new WorkspacePaneViewModel(entity, paneId);
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

        selectedView.Entities.Add(new ViewEntityViewModel(selectedViewEntity, indentLevel: 0, isParentContext: true));
        if (selectedViewEntity.Snapshot.Data is not JsonElement selectedViewData)
        {
            this.StickyParentContextText = selectedView.Title;
            return;
        }

        if (selectedViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (!this.EntityBroker.TryGetReferencedEntity(subView, "view-entity-id", out var subViewEntity)
                    || subViewEntity is null)
                {
                    continue;
                }

                selectedView.Entities.Add(new ViewEntityViewModel(subViewEntity, indentLevel: 1));
            }
        }

        this.StickyParentContextText = $"Parent Context: {selectedView.Title}";
    }

    private async Task OnActivateEntityAsync(
        object? parameter)
    {
        if (parameter is not ViewEntityViewModel entityView)
        {
            return;
        }

        var entity = entityView.Entity;

        if (string.Equals(entity.EntityType, "workspace", StringComparison.Ordinal))
        {
            await this.OpenWorkspaceAsync(
                new GetEntityRequest
                {
                    EntityId = entity.EntityId,
                });
            return;
        }

        await this.OpenEntityTabAsync(
            new GetEntityRequest
            {
                EntityId = entity.EntityId,
            });
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

    private async Task OpenWorkspaceAsync(
        GetEntityRequest workspaceRequest)
    {
        var loadingWorkspacePane = this.GetOrCreateLoadingWorkspacePane(workspaceRequest);
        this.SelectedWorkspacePane = loadingWorkspacePane;

        var workspaceSnapshot = await this.LoadSingleEntitySnapshotAsync(workspaceRequest);
        if (workspaceSnapshot?.Data is not JsonElement workspaceData)
        {
            return;
        }

        var existingWorkspace = this.WorkspacePanes.FirstOrDefault(
            pane => string.Equals(pane.Id, workspaceSnapshot.EntityId.ToString(), StringComparison.Ordinal));
        if (existingWorkspace is not null)
        {
            if (this.WorkspacePanes.Contains(loadingWorkspacePane))
            {
                this.WorkspacePanes.Remove(loadingWorkspacePane);
            }

            this.SelectedWorkspacePane = existingWorkspace;
            return;
        }

        var workspaceEntityRequests = this.BuildWorkspaceEntityRequests(workspaceData);
        var requests = new List<GetEntityRequest>
        {
            workspaceRequest,
        };
        requests.AddRange(workspaceEntityRequests);

        var entities = await this.EntityBroker!.GetEntitiesAsync(requests);
        var workspaceEntity = entities.FirstOrDefault(e => e.EntityId == workspaceSnapshot.EntityId);
        if (workspaceEntity is null)
        {
            return;
        }

        var workspacePane = this.CreateWorkspacePane(workspaceEntity, workspaceData);
        var loadingPaneIndex = this.WorkspacePanes.IndexOf(loadingWorkspacePane);
        if (loadingPaneIndex >= 0)
        {
            this.WorkspacePanes[loadingPaneIndex] = workspacePane;
        }
        else
        {
            this.WorkspacePanes.Add(workspacePane);
        }

        this.SelectedWorkspacePane = workspacePane;
    }

    private WorkspacePaneViewModel GetOrCreateLoadingWorkspacePane(
        GetEntityRequest workspaceRequest)
    {
        var paneId = $"{LoadingWorkspaceIdPrefix}{GetWorkspaceRequestKey(workspaceRequest)}";
        var existingPane = this.WorkspacePanes.FirstOrDefault(
            pane => string.Equals(pane.Id, paneId, StringComparison.Ordinal));
        if (existingPane is not null)
        {
            return existingPane;
        }

        var placeholderPane = this.WorkspacePanes.FirstOrDefault(
            pane => string.Equals(pane.Id, DefaultWorkspaceId, StringComparison.Ordinal));
        if (placeholderPane is not null)
        {
            this.WorkspacePanes.Remove(placeholderPane);
        }

        var displayName = $"Loading {GetWorkspaceRequestDisplayText(workspaceRequest)}...";
        var loadingPane = CreatePlaceholderWorkspacePane(paneId, displayName);
        this.WorkspacePanes.Add(loadingPane);
        return loadingPane;
    }

    private async Task OpenEntityTabAsync(
        GetEntityRequest entityRequest)
    {
        var entities = await this.EntityBroker!.GetEntitiesAsync([entityRequest]);
        var subscribedEntity = entities.FirstOrDefault();
        if (subscribedEntity is null)
        {
            return;
        }

        var selectedRegion = this.GetOrCreateSelectedWorkspaceRegion();
        var existingTab = selectedRegion.Tabs.FirstOrDefault(
            tab => string.Equals(tab.Id, subscribedEntity.EntityId.ToString(), StringComparison.Ordinal));
        if (existingTab is not null)
        {
            selectedRegion.SelectedTab = existingTab;
            return;
        }

        WorkspaceTabViewModel tab = new EntityWorkspaceTabViewModel
        {
            Id = subscribedEntity.EntityId.ToString(),
            Title = subscribedEntity.DisplayName,
            Entity = subscribedEntity,
        };

        selectedRegion.Tabs.Add(tab);
        selectedRegion.SelectedTab = tab;
    }

    private async Task OpenEntityBrowserTabAsync()
    {
        const string entityBrowserTabId = "entity-browser-tab";
        var selectedRegion = this.GetOrCreateSelectedWorkspaceRegion();
        var existingTab = selectedRegion.Tabs
            .OfType<EntityBrowserWorkspaceTabViewModel>()
            .FirstOrDefault(tab => string.Equals(tab.Id, entityBrowserTabId, StringComparison.Ordinal));
        if (existingTab is not null)
        {
            selectedRegion.SelectedTab = existingTab;
            return;
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

        selectedRegion.Tabs.Add(entityBrowserTab);
        selectedRegion.SelectedTab = entityBrowserTab;
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

    private WorkspacePaneViewModel CreateWorkspacePane(
        SubscribedEntityViewModel workspaceEntity,
        JsonElement workspaceData)
    {
        var workspacePane = new WorkspacePaneViewModel(workspaceEntity);
        var regions = new List<WorkspaceRegionViewModel>();

        if (workspaceData.TryGetProperty("regions", out var workspaceRegions)
            && workspaceRegions.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in workspaceRegions.EnumerateArray())
            {
                if (region.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var workspaceRegion = this.CreateWorkspaceRegion(region, workspaceEntity);
                regions.Add(workspaceRegion);
            }
        }

        workspacePane.SetRegions(regions);
        if (workspacePane.SelectedRegion is not null)
        {
            workspacePane.SelectedRegion.SelectedTab ??= workspacePane.SelectedRegion.Tabs.FirstOrDefault();
        }

        return workspacePane;
    }

    private WorkspaceRegionViewModel CreateWorkspaceRegion(
        JsonElement region,
        SubscribedEntityViewModel workspaceEntity)
    {
        var workspaceRegion = new WorkspaceRegionViewModel
        {
            Id = ReadString(region, "region-id") ?? "region",
            Title = ReadString(region, "title") ?? "Region",
            DockRegion = ReadString(region, "dock") ?? "center",
            RelativeSize = ReadDouble(region, "size") ?? 1,
        };

        if (region.TryGetProperty("tabs", out var tabs)
            && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (tab.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (this.TryReadWorkspaceTabContent(tab, out var workspaceTab))
                {
                    workspaceRegion.Tabs.Add(workspaceTab);
                }
            }
        }

        if (workspaceRegion.Tabs.Count == 0)
        {
            workspaceRegion.Tabs.Add(
                new EntityWorkspaceTabViewModel
                {
                    Id = workspaceEntity.EntityId.ToString(),
                    Title = workspaceEntity.DisplayName,
                    Entity = workspaceEntity,
                    DockRegion = "full",
                });
        }

        workspaceRegion.SelectedTab = workspaceRegion.Tabs[0];
        return workspaceRegion;
    }

    private bool TryReadWorkspaceTabContent(
        JsonElement tab,
        out WorkspaceTabViewModel workspaceTab)
    {
        workspaceTab = null!;
        if (!tab.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Try entity reference through the broker
        if (this.entityBroker?.TryGetReferencedEntity(content, "target-entity-name", out var targetEntity) == true
            && targetEntity is not null)
        {
            workspaceTab = new EntityWorkspaceTabViewModel
            {
                Id = ReadString(tab, "tab-id") ?? targetEntity.EntityId.ToString(),
                Title = ReadString(tab, "title") ?? targetEntity.DisplayName,
                Entity = targetEntity,
                DockRegion = ReadString(tab, "dock") ?? "full",
            };
            return true;
        }

        if (content.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            workspaceTab = new BrowserWorkspaceTabViewModel
            {
                Id = ReadString(tab, "tab-id") ?? url.GetString()!,
                Title = ReadString(tab, "title") ?? url.GetString()!,
                Url = url.GetString()!,
                DockRegion = ReadString(tab, "dock") ?? "full",
            };
            return true;
        }

        return false;
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

}
