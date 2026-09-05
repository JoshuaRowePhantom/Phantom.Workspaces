using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestLaunchpadViewModel : WorkspaceTabViewModel
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly IReadOnlyDictionary<string, string>? initialParameterValues;
    private readonly Task executorOptionsLoadTask;
    private bool canStart;

    public SubscribedEntityViewModel ManifestEntity { get; }

    public ObservableCollection<AgentManifestParameterRowViewModel> Parameters { get; } = [];

    public bool CanStart
    {
        get => this.canStart;
        private set => this.SetProperty(ref this.canStart, value);
    }

    public RelayCommand StartSessionCommand { get; }
    public RelayCommand EditManifestCommand { get; }

    public AgentManifestLaunchpadViewModel(
        SubscribedEntityViewModel manifestEntity,
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        MainWindowViewModel mainWindowViewModel,
        IReadOnlyDictionary<string, string>? initialParameterValues = null)
    {
        this.ManifestEntity = manifestEntity;
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
        this.mainWindowViewModel = mainWindowViewModel;
        this.initialParameterValues = initialParameterValues;

        this.StartSessionCommand = new RelayCommand(
            async _ => await this.StartSessionAsync(),
            _ => this.CanStart);
        this.EditManifestCommand = new RelayCommand(
            async _ => await this.EditManifestAsync());

        this.LoadParameters();
        this.UpdateCanStart();

        this.Parameters.CollectionChanged += this.OnParametersCollectionChanged;

        this.executorOptionsLoadTask = this.Parameters.Any(p => p.IsExecutorPicker)
            ? this.LoadExecutorOptionsAsync(this.Lifetime.Token)
            : Task.CompletedTask;

        if (this.Parameters.Count == 0)
        {
            Lifetime.Run(this.StartSessionAsync);
        }
    }

    /// <summary>
    /// Completes once the combined <c>executor</c> picker options (trust-profile and
    /// user-computer-profile entities) have been loaded (issue #1440). Exposed for deterministic tests.
    /// </summary>
    internal Task ExecutorOptionsLoaded => this.executorOptionsLoadTask;

    private void LoadParameters()
    {
        if (this.ManifestEntity.Data is not JsonElement data
            || !data.TryGetProperty("manifest", out var manifestElement))
        {
            return;
        }

        AgentManifest manifest;
        try
        {
            manifest = AgentManifestLoader.LoadManifestFromJson(manifestElement.GetRawText());
        }
        catch
        {
            return;
        }

        var parameters = manifest.Parameters?.Properties;
        if (parameters is null || parameters.Count == 0)
        {
            return;
        }

        foreach (var param in parameters)
        {
            var paramName = param.Name ?? string.Empty;
            var row = new AgentManifestParameterRowViewModel
            {
                Name = paramName,
                DisplayName = paramName,
                Description = param.Description ?? string.Empty,
                IsRequired = param.Required == true,
                ParameterKind = DetermineParameterKind(param.Kind, paramName),
            };

            if (this.initialParameterValues is not null
                && this.initialParameterValues.TryGetValue(paramName, out var initialValue))
            {
                row.Value = initialValue;
            }
            else if (param.Default is string defaultStr)
            {
                row.Value = defaultStr;
            }
            else if (param.Default is not null)
            {
                row.Value = param.Default.ToString() ?? string.Empty;
            }

            row.PropertyChanged += this.OnParameterPropertyChanged;
            this.Parameters.Add(row);
        }
    }

    private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentManifestParameterRowViewModel.Value)
            or nameof(AgentManifestParameterRowViewModel.IsValid))
        {
            this.UpdateCanStart();
        }
    }

    private void OnParametersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.UpdateCanStart();
    }

    private void UpdateCanStart()
    {
        this.CanStart = this.Parameters.Count == 0
            || this.Parameters.All(p => p.IsValid);
        this.StartSessionCommand.RaiseCanExecuteChanged();
    }

    private async Task StartSessionAsync(CancellationToken ct = default)
    {
        if (this.ManifestEntity.Data is not JsonElement data)
        {
            return;
        }

        var agentSessionId = Guid.NewGuid().ToString("n");

        // Collect parameter values
        var parameterValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameterSelections = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in this.Parameters)
        {
            if (row.IsExecutorPicker)
            {
                if (row.Selection is { } selection)
                {
                    parameterSelections[row.Name] = selection;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.Value))
            {
                parameterValues[row.Name] = row.Value;
            }
        }
        IReadOnlyDictionary<string, string>? parametersDict = parameterValues.Count > 0 ? parameterValues : null;
        IReadOnlyDictionary<string, JsonElement>? parameterSelectionsDict =
            parameterSelections.Count > 0 ? parameterSelections : null;

        var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            this.mainWindowViewModel,
            this.ManifestEntity,
            agentSessionId,
            parametersDict,
            parameterSelectionsDict);

        if (createdAgentSessionEntity is null)
        {
            return;
        }

        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = createdAgentSessionEntity.EntityId.ToString(),
            Title = createdAgentSessionEntity.DisplayName,
            DockRegion = "full",
            Entity = createdAgentSessionEntity,
            NotificationService = this.mainWindowViewModel.NotificationService,
            AgentSessionId = agentSessionId,
            WorkspacePaneId = this.mainWindowViewModel.SelectedWorkspacePane?.Id,
        };
        await this.mainWindowViewModel.OpenTabAsync(loadingTab);

        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

        if (data.TryGetProperty("manifest", out var manifestElement))
        {
            var manifestJson = manifestElement.GetRawText();
            // AgentChat must be constructed on the UI thread (issue #909); the work is fully
            // async, so running it here keeps the UI responsive without a Task.Run hop.
            // Route through IRunningAgentChatTable → AgentChatFactory.GetOrCreateAsync so
            // AgentChatFactory.WithSelfAsFactory injects itself as RunningAgentChatFactory on
            // AgentServices. The old direct AgentFactory.CreateAgentChatAsync call bypassed the
            // factory, leaving RunningAgentChatFactory null; the #1109 guard in AgentChat then
            // threw "AgentServices.RunningAgentChatFactory must be supplied at construction time"
            // as soon as a Copilot SDK client was resolved, surfacing as "Failed to load agent
            // session from manifest" (issue #1180).
            this.Lifetime.Run(_ => InitializeSessionTabAsync(async () =>
            {
                var loggerFactory = new ObservableLoggerFactory();
                var agentServices = await this.agentSessionShortcutContext
                    .CreateAgentServicesAsync(this.mainWindowViewModel, loggerFactory);
                var agentManifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
                // Populate the manifest's stable identity from the workspace entity so the
                // ManifestIdentity ("all sessions using this manifest") consent scope works for
                // real manifest entities, not just hand-authored JSON (issue #1401).
                agentManifest.Metadata ??= new Dictionary<string, object>();
                if (!agentManifest.Metadata.ContainsKey(AgentManifestSecretUseMemoryFactory.EntityIdMetadataKey))
                {
                    agentManifest.Metadata[AgentManifestSecretUseMemoryFactory.EntityIdMetadataKey] =
                        this.ManifestEntity.EntityId.ToString();
                }
                var lease = await this.openAgentSessionShortcutHandler.RunningAgentChatTable.AcquireAsync(
                    new AcquireAgentChatRequest
                    {
                        AgentSessionId = new AgentSessionId(agentSessionId),
                        AgentManifest = agentManifest,
                        Parameters = parametersDict,
                        AgentServices = agentServices,
                        ToolResourceFactory = agentServices.ToolResourceFactory,
                        ForegroundScheduler = foregroundScheduler,
                        EntityName = createdAgentSessionEntity.DisplayName,
                        EntityId = createdAgentSessionEntity.EntityId.ToString(),
                        WorkspaceId = loadingTab.WorkspacePaneId,
                    });
                loadingTab.SetLease(lease);
                return (lease.AgentChat, loggerFactory);
            }, createdAgentSessionEntity, loadingTab, foregroundScheduler));
        }
        else if (data.TryGetProperty("definition", out var definitionElement))
        {
            var definitionJson = definitionElement.GetRawText();
            // Same #1180 fix as the manifest branch: acquire through IRunningAgentChatTable so
            // the AgentChatFactory self-injects as RunningAgentChatFactory.
            this.Lifetime.Run(_ => InitializeSessionTabAsync(async () =>
            {
                var loggerFactory = new ObservableLoggerFactory();
                var agentServices = await this.agentSessionShortcutContext
                    .CreateAgentServicesAsync(this.mainWindowViewModel, loggerFactory);
                var agentDefinition = PhantomAgentSchema.AgentDefinitionFromJson(definitionJson);
                var lease = await this.openAgentSessionShortcutHandler.RunningAgentChatTable.AcquireAsync(
                    new AcquireAgentChatRequest
                    {
                        AgentSessionId = new AgentSessionId(agentSessionId),
                        AgentDefinition = agentDefinition,
                        AgentServices = agentServices,
                        ToolResourceFactory = agentServices.ToolResourceFactory,
                        ForegroundScheduler = foregroundScheduler,
                        EntityName = createdAgentSessionEntity.DisplayName,
                        EntityId = createdAgentSessionEntity.EntityId.ToString(),
                        WorkspaceId = loadingTab.WorkspacePaneId,
                    });
                loadingTab.SetLease(lease);
                return (lease.AgentChat, loggerFactory);
            }, createdAgentSessionEntity, loadingTab, foregroundScheduler));
        }
    }

    private async Task InitializeSessionTabAsync(
        Func<Task<(AgentChat AgentChat, ObservableLoggerFactory LoggerFactory)>> createChatAsync,
        SubscribedEntityViewModel createdAgentSessionEntity,
        AgentSessionWorkspaceTabViewModel loadingTab,
        TaskScheduler foregroundScheduler)
    {
        try
        {
            var (agentChat, loggerFactory) = await createChatAsync();
            // #1429: materialize through the single composition seam so slash commands are always wired.
            var agent = this.openAgentSessionShortcutHandler.ComposeSessionAgentViewModel(
                this.mainWindowViewModel, loggerFactory, agentChat, createdAgentSessionEntity, loadingTab, foregroundScheduler);
            loadingTab.SetReady(agent, loggerFactory);
        }
        catch (Exception ex)
        {
            loadingTab.SetFailed(ex.Message);
        }
    }

    private async Task EditManifestAsync()
    {
        await this.mainWindowViewModel.ShortcutManager.HandleShortcutAsync(
            this.mainWindowViewModel,
            Shortcut.Edit,
            this.ManifestEntity);
    }

    internal static AgentManifestParameterKind DetermineParameterKind(string parameterName)
    {
        return parameterName == "working-directory"
            ? AgentManifestParameterKind.Directory
            : AgentManifestParameterKind.Text;
    }

    /// <summary>
    /// Determines the launchpad row kind, honouring the manifest parameter's explicit <c>kind</c> field
    /// first (issue #1440, per-component-executor-binding) and falling back to name-based inference when
    /// no kind is declared. Replaces the earlier name-only heuristic that could never surface the
    /// <c>executor</c> picker.
    /// </summary>
    internal static AgentManifestParameterKind DetermineParameterKind(string? kind, string parameterName)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (string.Equals(kind, AgentManifestParameterKinds.Executor, StringComparison.Ordinal))
            {
                return AgentManifestParameterKind.Executor;
            }

            if (string.Equals(kind, "directory", StringComparison.Ordinal))
            {
                return AgentManifestParameterKind.Directory;
            }
        }

        return DetermineParameterKind(parameterName);
    }

    private async Task LoadExecutorOptionsAsync(CancellationToken ct = default)
    {
        var executorRows = this.Parameters.Where(p => p.IsExecutorPicker).ToList();
        if (executorRows.Count == 0)
        {
            return;
        }

        try
        {
            var dataAccessLayer = this.mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;

            var queryRequest = new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "trust-profiles" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = ["llm-trust-profile"] },
                        },
                    },
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "user-computer-profiles" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = ["user-computer-profile"] },
                        },
                    },
                ],
            };

            var queryResult = await dataAccessLayer.QueryAsync(queryRequest);
            var snapshotIds = queryResult.Batches
                .SelectMany(batch => batch.Entities)
                .Select(snapshot => snapshot.EntityId)
                .Distinct()
                .ToArray();

            var entities = await this.mainWindowViewModel.EntityBroker.GetEntitiesAsync(snapshotIds);

            var options = new List<ExecutorOptionViewModel>();
            foreach (var entity in entities)
            {
                if (entity.IsEntityType("user-computer-profile"))
                {
                    options.Add(new ExecutorOptionViewModel
                    {
                        Kind = ExecutorParameterSelection.UserComputerProfileKind,
                        DisplayName = $"{entity.DisplayName} (computer)",
                        Selection = ExecutorParameterSelection.ForUserComputerProfile(entity.EntityId.ToString()),
                    });
                }
                else if (entity.IsEntityType("llm-trust-profile"))
                {
                    options.Add(new ExecutorOptionViewModel
                    {
                        Kind = ExecutorParameterSelection.TrustProfileKind,
                        DisplayName = $"{entity.DisplayName} (trust policy)",
                        Selection = ExecutorParameterSelection.ForTrustProfile(GetTrustProfileNameOrId(entity)),
                    });
                }
            }

            foreach (var row in executorRows)
            {
                foreach (var option in options)
                {
                    row.ExecutorOptions.Add(option);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort population; leave the picker empty on failure (required rows stay invalid).
        }
    }

    private static string GetTrustProfileNameOrId(SubscribedEntityViewModel entity)
    {
        if (entity.Data is JsonElement data
            && data.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.GetArrayLength() > 0)
        {
            var first = names[0];
            if (first.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(first.GetString()))
            {
                return first.GetString()!;
            }

            if (first.ValueKind == JsonValueKind.Array)
            {
                var parts = first.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                if (parts.Length > 0)
                {
                    return parts[^1]!;
                }
            }
        }

        return entity.EntityId.ToString();
    }
}
