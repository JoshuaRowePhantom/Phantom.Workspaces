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
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestLaunchpadViewModel : WorkspaceTabViewModel
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly IReadOnlyDictionary<string, string>? initialParameterValues;
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

        if (this.Parameters.Count == 0)
        {
            Lifetime.Run(this.StartSessionAsync);
        }
    }

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
                ParameterKind = DetermineParameterKind(paramName),
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
        foreach (var row in this.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(row.Value))
            {
                parameterValues[row.Name] = row.Value;
            }
        }
        IReadOnlyDictionary<string, string>? parametersDict = parameterValues.Count > 0 ? parameterValues : null;

        var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            this.mainWindowViewModel,
            this.ManifestEntity,
            agentSessionId,
            parametersDict);

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
                var agentDefinition = AgentDefinition.FromJson(definitionJson);
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
            var agent = this.openAgentSessionShortcutHandler.BuildAgentViewModelPublic(
                this.mainWindowViewModel, loggerFactory, agentChat, createdAgentSessionEntity.DisplayName, loadingTab.Id, foregroundScheduler);
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
}
