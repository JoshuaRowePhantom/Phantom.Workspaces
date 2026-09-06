using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestLaunchpadViewModel : WorkspaceTabViewModel
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly Task executorOptionsLoadTask;
    private bool canStart;

    public SubscribedEntityViewModel ManifestEntity { get; }

    /// <summary>
    /// The hosted, reusable manifest-parameter component (issue #1463). Owns the parameter rows,
    /// their validation aggregate, value collection, and persist-by-name behavior.
    /// </summary>
    public ManifestParametersViewModel ManifestParameters { get; }

    /// <summary>Passthrough to the hosted component's parameter rows for view binding and tests.</summary>
    public ObservableCollection<AgentManifestParameterRowViewModel> Parameters => this.ManifestParameters.Parameters;

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

        this.ManifestParameters = new ManifestParametersViewModel(mainWindowViewModel, initialParameterValues);
        this.ManifestParameters.PropertyChanged += this.OnManifestParametersPropertyChanged;

        this.StartSessionCommand = new RelayCommand(
            async _ => await this.StartSessionAsync(),
            _ => this.CanStart);
        this.EditManifestCommand = new RelayCommand(
            async _ => await this.EditManifestAsync());

        this.ManifestParameters.SetManifest(this.ManifestEntity);
        this.UpdateCanStart();

        this.executorOptionsLoadTask = this.ManifestParameters.ExecutorOptionsLoaded;

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

    private void OnManifestParametersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManifestParametersViewModel.IsValid))
        {
            this.UpdateCanStart();
        }
    }

    private void UpdateCanStart()
    {
        this.CanStart = this.ManifestParameters.IsValid;
        this.StartSessionCommand.RaiseCanExecuteChanged();
    }

    private async Task StartSessionAsync(CancellationToken ct = default)
    {
        if (this.ManifestEntity.Data is not JsonElement data)
        {
            return;
        }

        var agentSessionId = Guid.NewGuid().ToString("n");

        // Collect parameter values through the hosted component, then commit the launch so retained
        // by-name values for parameters absent from this manifest are pruned (issue #1463).
        var parameterValues = this.ManifestParameters.GetValues();
        var parameterSelections = this.ManifestParameters.GetSelections();
        this.ManifestParameters.CommitLaunch();

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

}
