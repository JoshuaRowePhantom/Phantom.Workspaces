using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const string PlaceholderWorkspaceId = "loading-workspace";
    private const string LoadingWorkspaceIdPrefix = "loading-workspace:";
    private static readonly ViewDefinitionViewModel EmptyView = new()
    {
        Id = "empty",
        Title = "No views",
        Description = string.Empty,
        IconGlyph = "◻",
    };
    private readonly Task<EntityRepository> entityRepositoryTask;
    private EntityRepository? entityRepository;
    private EntityBroker? entityBroker;
    private readonly ThemeProfileStore themeProfileStore;
    private readonly DispatcherTimer refreshTimer;
    private ViewDefinitionViewModel selectedTopLevelView = EmptyView;
    private WorkspacePaneViewModel selectedWorkspacePane;
    private string stickyParentContextText = string.Empty;
    private string selectedThemeName = "Dark";

    public MainWindowViewModel(
        RepositorySource repositorySource)
    {
        this.RepositorySource = repositorySource;
        this.entityRepositoryTask = EntityRepository.CreateAsync(repositorySource);
        this.themeProfileStore = ThemeProfileStore.ForCurrentUser();

        this.TopLevelViews = new ObservableCollection<ViewDefinitionViewModel>();
        this.VisibleEntities = new ObservableCollection<EntityVisualViewModel>();
        this.WorkspacePanes = new ObservableCollection<WorkspacePaneViewModel>
        {
            CreatePlaceholderWorkspacePane(PlaceholderWorkspaceId, "Loading workspace..."),
        };

        this.selectedWorkspacePane = this.WorkspacePanes[0];
        this.ActivateEntityCommand = new RelayCommand(async _ => await this.OnActivateEntityAsync(_));
        this.SetDarkThemeCommand = new RelayCommand(async _ => await this.SetThemeAsync("Dark"));
        this.SetLightThemeCommand = new RelayCommand(async _ => await this.SetThemeAsync("Light"));

        this.refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        this.refreshTimer.Tick += this.OnRefreshTick;
    }

    public RepositorySource RepositorySource { get; }

    public ObservableCollection<ViewDefinitionViewModel> TopLevelViews { get; }

    public ObservableCollection<EntityVisualViewModel> VisibleEntities { get; }

    public ObservableCollection<WorkspacePaneViewModel> WorkspacePanes { get; }

    public RelayCommand ActivateEntityCommand { get; }

    public RelayCommand SetDarkThemeCommand { get; }

    public RelayCommand SetLightThemeCommand { get; }

    public bool IsDarkThemeSelected => string.Equals(this.selectedThemeName, "Dark", StringComparison.Ordinal);

    public bool IsLightThemeSelected => string.Equals(this.selectedThemeName, "Light", StringComparison.Ordinal);

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
        _ => "In-memory repository source.",
    };

    public bool HasStickyParentContext => !string.IsNullOrWhiteSpace(this.StickyParentContextText);

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

    private EntityRepository EntityRepository => this.entityRepository
        ?? throw new InvalidOperationException("The view model has not been initialized.");

    private EntityBroker EntityBroker => this.entityBroker
        ?? throw new InvalidOperationException("The view model has not been initialized.");

    public async Task InitializeAsync()
    {
        this.entityRepository = await this.entityRepositoryTask;
        this.entityBroker = new EntityBroker(this.EntityRepository);
        this.entityBroker.Changed += this.OnEntityBrokerChanged;

        await this.EntityBroker.InitializeAsync();
        await this.LoadNavigationSubscriptionAsync();
        await this.RebuildViewsFromRepositoryAsync();
        await this.InitializeThemeAsync();
        await this.OpenStartupWorkspaceAsync();
        this.refreshTimer.Start();
    }

    private async Task InitializeThemeAsync()
    {
        var resolvedTheme = await this.themeProfileStore.GetOrInitializeThemeAsync();
        this.ApplyTheme(normalizeToDisplayName: true, resolvedTheme);
    }

    private async Task SetThemeAsync(
        string themeName)
    {
        this.ApplyTheme(normalizeToDisplayName: false, themeName);
        await this.themeProfileStore.SetThemeAsync(themeName);
    }

    private void ApplyTheme(
        bool normalizeToDisplayName,
        string themeName)
    {
        var normalizedDisplayName = normalizeToDisplayName
            ? string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark"
            : themeName;
        if (!this.SetProperty(ref this.selectedThemeName, normalizedDisplayName, nameof(this.SelectedThemeName)))
        {
            this.RaisePropertyChanged(nameof(this.IsDarkThemeSelected));
            this.RaisePropertyChanged(nameof(this.IsLightThemeSelected));
            return;
        }

        Application.Current!.RequestedThemeVariant = string.Equals(this.selectedThemeName, "Light", StringComparison.Ordinal)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
        this.RaisePropertyChanged(nameof(this.IsDarkThemeSelected));
        this.RaisePropertyChanged(nameof(this.IsLightThemeSelected));
    }

    public string SelectedThemeName => this.selectedThemeName;

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

    private async Task RebuildViewsFromRepositoryAsync()
    {
        var mainViewRequest = new GetEntityRequest
        {
            EntityName = new EntityName("views", "main"),
        };

        var mainViewSnapshot = await this.LoadSingleEntitySnapshotAsync(mainViewRequest);
        if (mainViewSnapshot?.Data is not JsonElement mainViewData
            || !mainViewData.TryGetProperty("sub-views", out var subViews)
            || subViews.ValueKind != JsonValueKind.Array)
        {
            this.TopLevelViews.Clear();
            this.TopLevelViews.Add(EmptyView);
            this.SelectedTopLevelView = EmptyView;
            return;
        }

        var existingSelectionId = this.SelectedTopLevelView?.Id;
        var nextViews = new List<ViewDefinitionViewModel>();

        foreach (var subView in subViews.EnumerateArray())
        {
            if (!TryReadEntityRequest(subView, "view-entity-id", out var viewRequest))
            {
                continue;
            }

            var viewSnapshot = await this.LoadSingleEntitySnapshotAsync(viewRequest);

            if (viewSnapshot?.Data is JsonElement viewData)
            {
                nextViews.Add(
                    new ViewDefinitionViewModel
                    {
                        Id = viewSnapshot.EntityId.ToString(),
                        Title = ReadLocalString(viewData, "title")
                            ?? ReadLocalString(viewData, "display-name")
                            ?? "View",
                        Description = ReadPrimaryName(viewData) ?? "Repository view",
                        IconGlyph = "◻",
                    });
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
            this.SelectedTopLevelView = EmptyView;
            this.VisibleEntities.Clear();
            this.StickyParentContextText = string.Empty;
            return;
        }

        this.SelectedTopLevelView = this.TopLevelViews.FirstOrDefault(
            view => string.Equals(view.Id, existingSelectionId, StringComparison.Ordinal))
            ?? this.TopLevelViews[0];
    }

    private async Task ApplySelectedViewAsync()
    {
        var selectedView = this.selectedTopLevelView ?? EmptyView;
        this.VisibleEntities.Clear();
        if (string.Equals(selectedView.Id, EmptyView.Id, StringComparison.Ordinal))
        {
            this.StickyParentContextText = string.Empty;
            return;
        }

        if (selectedView.IsEntityBrowser)
        {
            var allSnapshots = await this.EntityRepository.ExportEntitySnapshotsAsync();

            foreach (var snapshot in allSnapshots.Values.OrderBy(static snapshot => snapshot.EntityId.Value))
            {
                if (snapshot.Data is not JsonElement data)
                {
                    continue;
                }

                this.VisibleEntities.Add(CreateEntityVisual(snapshot.EntityId, data, indentLevel: 0));
            }

            this.StickyParentContextText = "Entity Browser";
            return;
        }

        if (!Guid.TryParse(selectedView.Id, out var selectedViewIdGuid))
        {
            this.StickyParentContextText = selectedView.Title;
            return;
        }

        var selectedViewId = new EntityId(selectedViewIdGuid);
        var selectedViewSnapshot = await this.LoadSingleEntitySnapshotAsync(
            new GetEntityRequest { EntityId = selectedViewId });
        
        if (selectedViewSnapshot?.Data is not JsonElement selectedViewData)
        {
            this.StickyParentContextText = selectedView.Title;
            return;
        }

        this.VisibleEntities.Add(CreateEntityVisual(selectedViewSnapshot.EntityId, selectedViewData, indentLevel: 0, isParentContext: true));
        if (selectedViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (!TryReadEntityRequest(subView, "view-entity-id", out var viewRequest))
                {
                    continue;
                }

                var viewSnapshot = await this.LoadSingleEntitySnapshotAsync(viewRequest);

                if (viewSnapshot?.Data is not JsonElement viewData)
                {
                    continue;
                }

                this.VisibleEntities.Add(CreateEntityVisual(viewSnapshot.EntityId, viewData, indentLevel: 1));
            }
        }

        this.StickyParentContextText = $"Parent Context: {selectedView.Title}";
    }

    private async Task OnActivateEntityAsync(
        object? parameter)
    {
        if (parameter is not EntityVisualViewModel entity)
        {
            return;
        }

        if (string.Equals(entity.EntityType, "workspace", StringComparison.Ordinal))
        {
            if (Guid.TryParse(entity.EntityId, out var workspaceGuid))
            {
                await this.OpenWorkspaceAsync(
                    new GetEntityRequest
                    {
                        EntityId = new EntityId(workspaceGuid),
                    });
            }
            return;
        }

        if (Guid.TryParse(entity.EntityId, out var entityGuid))
        {
            await this.OpenEntityTabAsync(
                new GetEntityRequest
                {
                    EntityId = new EntityId(entityGuid),
                });
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
        _ = this.RebuildViewsFromRepositoryAsync();
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
            pane => string.Equals(pane.Id, PlaceholderWorkspaceId, StringComparison.Ordinal));
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

        WorkspaceTabViewModel tab = string.Equals(subscribedEntity.EntityType, "note", StringComparison.Ordinal)
            ? new NoteWorkspaceTabViewModel
            {
                Id = subscribedEntity.EntityId.ToString(),
                Title = subscribedEntity.DisplayName,
                Markdown = string.Join(Environment.NewLine, subscribedEntity.DisplayItems),
                Entity = subscribedEntity,
            }
            : new EntityWorkspaceTabViewModel
            {
                Id = subscribedEntity.EntityId.ToString(),
                Title = subscribedEntity.DisplayName,
                Entity = subscribedEntity,
            };

        selectedRegion.Tabs.Add(tab);
        selectedRegion.SelectedTab = tab;
    }

    private async Task<EntitySnapshot?> LoadSingleEntitySnapshotAsync(
        GetEntityRequest request)
    {
        var getResult = await this.EntityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [request],
            });

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault();
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

                var workspaceRegion = this.CreateWorkspaceRegion(region);
                regions.Add(workspaceRegion);
            }
        }

        if (regions.Count == 0)
        {
            var fallbackRegion = new WorkspaceRegionViewModel
            {
                Id = "center",
                Title = "Center",
                DockRegion = "center",
                RelativeSize = 1,
            };
            fallbackRegion.Tabs.Add(
                new NoteWorkspaceTabViewModel
                {
                    Id = workspaceEntity.EntityId.ToString(),
                    Title = workspaceEntity.DisplayName,
                    Markdown = string.Join(Environment.NewLine, workspaceEntity.DisplayItems),
                    Entity = workspaceEntity,
                    DockRegion = "full",
                });
            fallbackRegion.SelectedTab = fallbackRegion.Tabs[0];
            regions.Add(fallbackRegion);
        }

        workspacePane.SetRegions(regions);
        if (workspacePane.SelectedRegion is not null)
        {
            workspacePane.SelectedRegion.SelectedTab ??= workspacePane.SelectedRegion.Tabs.FirstOrDefault();
        }

        return workspacePane;
    }

    private WorkspaceRegionViewModel CreateWorkspaceRegion(
        JsonElement region)
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
                new NoteWorkspaceTabViewModel
                {
                    Id = workspaceRegion.Id,
                    Title = workspaceRegion.Title,
                    Markdown = string.Empty,
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
            workspaceTab = targetEntity.EntityType == "note"
                ? new NoteWorkspaceTabViewModel
                {
                    Id = ReadString(tab, "tab-id") ?? targetEntity.EntityId.ToString(),
                    Title = ReadString(tab, "title") ?? targetEntity.DisplayName,
                    Markdown = string.Join(Environment.NewLine, targetEntity.DisplayItems),
                    Entity = targetEntity,
                    DockRegion = ReadString(tab, "dock") ?? "full",
                }
                : new EntityWorkspaceTabViewModel
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

    private async Task LoadNavigationSubscriptionAsync()
    {
        var mainViewRequest = new GetEntityRequest
        {
            EntityName = new EntityName("views", "main"),
        };

        var mainViewSnapshot = await this.LoadSingleEntitySnapshotAsync(mainViewRequest);
        if (mainViewSnapshot?.Data is not JsonElement mainViewData)
        {
            return;
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
    }

    private static EntityVisualViewModel CreateEntityVisual(
        EntityId entityId,
        JsonElement element,
        int indentLevel,
        bool isParentContext = false)
    {
        var entityType = ReadFirstEntityType(element) ?? "entity";
        var displayName = ReadLocalString(element, "display-name")
            ?? ReadLocalString(element, "title")
            ?? ReadPrimaryName(element)
            ?? entityId.ToString();

        var visual = new EntityVisualViewModel
        {
            EntityId = entityId.ToString(),
            EntityType = entityType,
            DisplayName = displayName,
            IndentLevel = indentLevel,
            IsParentContext = isParentContext,
        };

        if (element.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badges.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String))
            {
                visual.Badges.Add(badge.GetString()!);
            }
        }

        if (element.TryGetProperty("shortcuts", out var shortcuts) && shortcuts.ValueKind == JsonValueKind.Array)
        {
            foreach (var shortcut in shortcuts.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String))
            {
                visual.Shortcuts.Add(shortcut.GetString()!);
            }
        }

        var markdown = ReadString(element, "markdown");
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            visual.DisplayItems.Add(markdown);
        }

        return visual;
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

    private static string? ReadFirstEntityType(
        JsonElement element)
    {
        if (!element.TryGetProperty("entity-types", out var types)
            || types.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var type in types.EnumerateArray())
        {
            if (type.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(type.GetString()))
            {
                return type.GetString();
            }
        }

        return null;
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
        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var components = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return components.Length > 0 ? new EntityName(components) : null;
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            return property.TryReadEntityName();
        }

        return null;
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
