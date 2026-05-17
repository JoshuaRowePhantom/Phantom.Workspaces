using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
            new()
            {
                Id = "workspace-main",
                Title = "Main Workspace",
            },
        };

        this.selectedWorkspacePane = this.WorkspacePanes[0];
        this.selectedWorkspacePane.Regions.Add(
            new WorkspaceRegionViewModel
            {
                Id = "center",
                Title = "Center",
                DockRegion = "center",
                RelativeSize = 1,
            });
        this.selectedWorkspacePane.SelectedRegion = this.selectedWorkspacePane.Regions[0];
        this.ActivateEntityCommand = new RelayCommand(this.OnActivateEntity);
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

            this.ApplySelectedView();
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
        this.RebuildViewsFromRepository();
        await this.InitializeThemeAsync();
        this.LoadStartupWorkspaceFromEntities();
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

    private void RebuildViewsFromRepository()
    {
        var snapshotsById = this.EntityBroker.SnapshotsById;
        if (snapshotsById.Count == 0)
        {
            return;
        }

        var existingSelectionId = this.SelectedTopLevelView?.Id;
        var nextViews = this.BuildTopLevelViews(snapshotsById);
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

    private IReadOnlyCollection<ViewDefinitionViewModel> BuildTopLevelViews(
        IReadOnlyDictionary<EntityId, EntitySnapshot> snapshotsById)
    {
        var built = new List<ViewDefinitionViewModel>();
        var mainView = this.EntityRepository.TryGetEntityByName(snapshotsById, ["views", "main"]);
        if (mainView?.Data is JsonElement mainViewData
            && mainViewData.TryGetProperty("sub-views", out var subViews)
            && subViews.ValueKind == JsonValueKind.Array)
        {
            foreach (var subView in subViews.EnumerateArray())
            {
                if (!TryReadEntityReference(subView, "view-entity-id", out var reference))
                {
                    continue;
                }

                var viewSnapshot = ResolveEntityReference(snapshotsById, reference);
                if (viewSnapshot?.Data is not JsonElement viewData)
                {
                    continue;
                }

                built.Add(
                    new ViewDefinitionViewModel
                    {
                        Id = viewSnapshot.EntityId.Value.ToString("D"),
                        Title = ReadLocalString(viewData, "title")
                            ?? ReadLocalString(viewData, "display-name")
                            ?? "View",
                        Description = ReadPrimaryName(viewData) ?? "Repository view",
                        IconGlyph = "◻",
                    });
            }
        }

        built.Add(
            new ViewDefinitionViewModel
            {
                Id = "entity-browser",
                Title = "Entity Browser",
                Description = "Dedicated browser/search (not view-driven).",
                IconGlyph = "⌕",
                IsEntityBrowser = true,
            });
        return built;
    }

    private void ApplySelectedView()
    {
        var selectedView = this.selectedTopLevelView ?? EmptyView;
        this.VisibleEntities.Clear();
        if (string.Equals(selectedView.Id, EmptyView.Id, StringComparison.Ordinal))
        {
            this.StickyParentContextText = string.Empty;
            return;
        }

        var snapshotsById = this.EntityBroker.SnapshotsById;
        if (selectedView.IsEntityBrowser)
        {
            foreach (var snapshot in snapshotsById.Values.OrderBy(static snapshot => snapshot.EntityId.Value))
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
        if (!snapshotsById.TryGetValue(selectedViewId, out var selectedViewSnapshot)
            || selectedViewSnapshot.Data is not JsonElement selectedViewData)
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
                if (!TryReadEntityReference(subView, "view-entity-id", out var reference))
                {
                    continue;
                }

                var viewSnapshot = ResolveEntityReference(snapshotsById, reference);
                if (viewSnapshot?.Data is not JsonElement viewData)
                {
                    continue;
                }

                this.VisibleEntities.Add(CreateEntityVisual(viewSnapshot.EntityId, viewData, indentLevel: 1));
            }
        }

        this.StickyParentContextText = $"Parent Context: {selectedView.Title}";
    }

    private void OnActivateEntity(
        object? parameter)
    {
        if (parameter is not EntityVisualViewModel entity)
        {
            return;
        }

        if (string.Equals(entity.EntityType, "workspace", StringComparison.Ordinal))
        {
            var existingWorkspace = this.WorkspacePanes.FirstOrDefault(
                pane => string.Equals(pane.Id, entity.EntityId, StringComparison.Ordinal));
            if (existingWorkspace is null)
            {
                existingWorkspace = new WorkspacePaneViewModel
                {
                    Id = entity.EntityId,
                    Title = entity.DisplayName,
                };
                existingWorkspace.Regions.Add(
                    new WorkspaceRegionViewModel
                    {
                        Id = "center",
                        Title = "Center",
                        DockRegion = "center",
                        RelativeSize = 1,
                    });
                existingWorkspace.SelectedRegion = existingWorkspace.Regions[0];
                this.WorkspacePanes.Add(existingWorkspace);
            }

            this.SelectedWorkspacePane = existingWorkspace;
            return;
        }

        var selectedRegion = this.GetOrCreateSelectedWorkspaceRegion();
        var existingTab = selectedRegion.Tabs.FirstOrDefault(
            tab => string.Equals(tab.Id, entity.EntityId, StringComparison.Ordinal));
        if (existingTab is not null)
        {
            selectedRegion.SelectedTab = existingTab;
            return;
        }

        WorkspaceTabViewModel tab = string.Equals(entity.EntityType, "note", StringComparison.Ordinal)
            ? new NoteWorkspaceTabViewModel
            {
                Id = entity.EntityId,
                Title = entity.DisplayName,
                Markdown = string.Join(Environment.NewLine, entity.DisplayItems),
            }
            : new EntityWorkspaceTabViewModel
            {
                Id = entity.EntityId,
                Title = entity.DisplayName,
                Entity = entity,
            };

        selectedRegion.Tabs.Add(tab);
        selectedRegion.SelectedTab = tab;
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
        this.RebuildViewsFromRepository();
    }

    private void LoadStartupWorkspaceFromEntities()
    {
        var snapshotsById = this.EntityBroker.SnapshotsById;
        var noteSnapshot = this.EntityRepository.TryGetEntityByName(snapshotsById, ["documentation", "getting-started"]);
        var workspaceSnapshot = this.EntityRepository.TryGetEntityByName(snapshotsById, ["documentation", "getting-started-workspace"]);
        if (noteSnapshot?.Data is not JsonElement noteData
            || workspaceSnapshot?.Data is not JsonElement workspaceData)
        {
            return;
        }

        var noteEntity = CreateEntityVisual(noteSnapshot.EntityId, noteData, indentLevel: 0);
        var workspaceId = ReadString(workspaceData, "entity-id") ?? "getting-started-workspace";
        var workspaceTitle = ReadLocalString(workspaceData, "display-name")
            ?? ReadLocalString(workspaceData, "title")
            ?? "Getting Started";
        var noteName = ReadPrimaryName(noteData);

        var workspacePane = this.WorkspacePanes.FirstOrDefault(p => string.Equals(p.Id, workspaceId, StringComparison.Ordinal));
        if (workspacePane is null)
        {
            workspacePane = new WorkspacePaneViewModel
            {
                Id = workspaceId,
                Title = workspaceTitle,
            };
            this.WorkspacePanes.Add(workspacePane);
        }

        workspacePane.Regions.Clear();
        if (workspaceData.TryGetProperty("regions", out var regions)
            && regions.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in regions.EnumerateArray())
            {
                if (region.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var workspaceRegion = CreateWorkspaceRegion(region, noteName, noteEntity);
                workspacePane.Regions.Add(workspaceRegion);
            }
        }

        if (workspacePane.Regions.Count == 0)
        {
            var centerRegion = new WorkspaceRegionViewModel
            {
                Id = "center",
                Title = "Center",
                DockRegion = "center",
                RelativeSize = 1,
            };
            centerRegion.Tabs.Add(
                new NoteWorkspaceTabViewModel
                {
                    Id = noteEntity.EntityId,
                    Title = noteEntity.DisplayName,
                    Markdown = string.Join(Environment.NewLine, noteEntity.DisplayItems),
                    DockRegion = "full",
                });
            centerRegion.SelectedTab = centerRegion.Tabs[0];
            workspacePane.Regions.Add(centerRegion);
        }

        workspacePane.SelectedRegion = workspacePane.Regions[0];
        workspacePane.SelectedRegion.SelectedTab ??= workspacePane.SelectedRegion.Tabs.FirstOrDefault();
        this.SelectedWorkspacePane = workspacePane;
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
            ?? entityId.Value.ToString("D");

        var visual = new EntityVisualViewModel
        {
            EntityId = entityId.Value.ToString("D"),
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

    private WorkspaceRegionViewModel CreateWorkspaceRegion(
        JsonElement region,
        string? noteName,
        EntityVisualViewModel noteEntity)
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

                if (TryReadWorkspaceTabContent(tab, noteName, noteEntity, out var workspaceTab))
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
                    Id = noteEntity.EntityId,
                    Title = noteEntity.DisplayName,
                    Markdown = string.Join(Environment.NewLine, noteEntity.DisplayItems),
                    DockRegion = "full",
                });
        }

        workspaceRegion.SelectedTab = workspaceRegion.Tabs[0];
        return workspaceRegion;
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

    private static EntitySnapshot? ResolveEntityReference(
        IReadOnlyDictionary<EntityId, EntitySnapshot> snapshotsById,
        EntityReference reference)
    {
        if (reference.EntityId is EntityId entityId
            && snapshotsById.TryGetValue(entityId, out var resolvedById))
        {
            return resolvedById;
        }

        if (reference.NameKey is not null)
        {
            foreach (var snapshot in snapshotsById.Values)
            {
                if (snapshot.Data is not JsonElement data)
                {
                    continue;
                }

                if (!TryReadNames(data, out var nameKeys))
                {
                    continue;
                }

                if (nameKeys.Contains(reference.NameKey, StringComparer.Ordinal))
                {
                    return snapshot;
                }
            }
        }

        return null;
    }

    private static bool TryReadEntityReference(
        JsonElement parent,
        string propertyName,
        out EntityReference reference)
    {
        reference = default;
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var stringValue = value.GetString();
            if (Guid.TryParse(stringValue, out var entityGuid))
            {
                reference = new EntityReference
                {
                    EntityId = new EntityId(entityGuid),
                };
                return true;
            }

            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                reference = new EntityReference
                {
                    NameKey = stringValue,
                };
                return true;
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var components = value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (components.Length == 0)
        {
            return false;
        }

        reference = new EntityReference
        {
            NameKey = string.Join("/", components!),
        };
        return true;
    }

    private static bool TryReadNames(
        JsonElement entityData,
        out IReadOnlyCollection<string> names)
    {
        var resolved = new List<string>();
        if (!entityData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            names = resolved;
            return false;
        }

        foreach (var nameElement in namesElement.EnumerateArray())
        {
            if (nameElement.ValueKind == JsonValueKind.String)
            {
                var value = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    resolved.Add(value);
                }
                continue;
            }

            if (nameElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = nameElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
            if (components.Length > 0)
            {
                resolved.Add(string.Join("/", components!));
            }
        }

        names = resolved;
        return resolved.Count > 0;
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

    private static bool TryReadWorkspaceTabContent(
        JsonElement tab,
        string? noteName,
        EntityVisualViewModel noteEntity,
        out WorkspaceTabViewModel workspaceTab)
    {
        workspaceTab = null!;
        if (!tab.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (content.TryGetProperty("target-entity-name", out var targetEntityName))
        {
            var targetName = ReadEntityReferenceText(targetEntityName);
            if (!string.IsNullOrWhiteSpace(targetName)
                && string.Equals(targetName, noteName, StringComparison.Ordinal))
            {
                workspaceTab = new NoteWorkspaceTabViewModel
                {
                    Id = ReadString(tab, "tab-id") ?? noteEntity.EntityId,
                    Title = ReadString(tab, "title") ?? noteEntity.DisplayName,
                    Markdown = string.Join(Environment.NewLine, noteEntity.DisplayItems),
                    DockRegion = ReadString(tab, "dock") ?? "full",
                };
                return true;
            }
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

    private static string? ReadEntityReferenceText(
        JsonElement property)
    {
        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            var parts = property.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join("/", parts!);
        }

        return null;
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

        if (property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("default", out var defaultValue)
            && defaultValue.ValueKind == JsonValueKind.String)
        {
            return defaultValue.GetString();
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

    private readonly record struct EntityReference
    {
        public EntityId? EntityId { get; init; }

        public string? NameKey { get; init; }
    }
}
