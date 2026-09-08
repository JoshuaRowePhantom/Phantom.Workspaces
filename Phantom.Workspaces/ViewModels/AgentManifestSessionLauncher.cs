using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Shared agent-session launch flow (issue #1461). Extracted from
/// <see cref="AgentManifestLaunchpadViewModel"/> so multiple hosts — the launchpad and the
/// workspace "New Agent" selection tab (<see cref="NewAgentSelectionViewModel"/>) — materialize an
/// agent session identically: create the agent-session entity through
/// <see cref="AgentSessionShortcutContext.CreateAgentSessionEntityAsync"/>, open the
/// <see cref="AgentSessionWorkspaceTabViewModel"/> in the target workspace pane, then construct the
/// <see cref="AgentChat"/> through <see cref="IRunningAgentChatTable"/> so
/// <see cref="AgentServices.RunningAgentChatFactory"/> is injected before construction (the #1180 /
/// #1109 fix). Callers are responsible for collecting parameter values/selections from their hosted
/// <see cref="ManifestParametersViewModel"/> and calling <see cref="ManifestParametersViewModel.CommitLaunch"/>
/// before invoking this helper.
/// </summary>
internal static class AgentManifestSessionLauncher
{
    /// <summary>
    /// Launches <paramref name="agentSourceEntity"/> (a manifest or definition entity) as an agent
    /// session with the supplied parameter values/selections, opening the resulting session tab in
    /// <paramref name="workspacePaneId"/> (or the selected pane when null). Returns the created
    /// agent-session entity, or <see langword="null"/> when the entity could not be created.
    /// </summary>
    public static async Task<SubscribedEntityViewModel?> LaunchAsync(
        ViewModelLifetime lifetime,
        MainWindowViewModel mainWindowViewModel,
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        SubscribedEntityViewModel agentSourceEntity,
        IReadOnlyDictionary<string, string>? parameterValues,
        IReadOnlyDictionary<string, JsonElement>? parameterSelections,
        string? workspacePaneId = null)
    {
        if (agentSourceEntity.Data is not JsonElement data)
        {
            return null;
        }

        var agentSessionId = Guid.NewGuid().ToString("n");
        JsonElement? sessionExecutor = null;
        JsonElement? executorComponentBindings = null;
        if (data.TryGetProperty("manifest", out var persistedManifest))
        {
            var executorResources = ExecutorResource.ParseManifestResources(persistedManifest.GetRawText());
            if (executorResources.Count > 0)
            {
                var trustProfile = await ResolveSelectedTrustProfileAsync(
                    mainWindowViewModel,
                    parameterSelections);
                var bindings = ExecutorBindings.Build(
                    executorResources,
                    parameterSelections ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                    trustProfile);
                sessionExecutor = bindings.SessionExecutor;
                executorComponentBindings = bindings.ToPersistableMap();
            }
        }

        var createdAgentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            mainWindowViewModel,
            agentSourceEntity,
            agentSessionId,
            parameterValues,
            parameterSelections,
            sessionExecutor: sessionExecutor,
            executorComponentBindings: executorComponentBindings);

        if (createdAgentSessionEntity is null)
        {
            return null;
        }

        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = createdAgentSessionEntity.EntityId.ToString(),
            Title = createdAgentSessionEntity.DisplayName,
            DockRegion = "full",
            Entity = createdAgentSessionEntity,
            NotificationService = mainWindowViewModel.NotificationService,
            AgentSessionId = agentSessionId,
            WorkspacePaneId = workspacePaneId ?? mainWindowViewModel.SelectedWorkspacePane?.Id,
        };
        await mainWindowViewModel.OpenTabAsync(loadingTab, workspacePaneId: workspacePaneId);

        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

        if (data.TryGetProperty("manifest", out var manifestElement))
        {
            var manifestJson = manifestElement.GetRawText();
            // AgentChat must be constructed on the UI thread (issue #909); the work is fully
            // async, so running it here keeps the UI responsive without a Task.Run hop.
            // Route through IRunningAgentChatTable → AgentChatFactory.GetOrCreateAsync so
            // AgentChatFactory.WithSelfAsFactory injects itself as RunningAgentChatFactory on
            // AgentServices (fix for #1180 / the #1109 guard).
            lifetime.Run(_ => InitializeSessionTabAsync(
                openAgentSessionShortcutHandler,
                mainWindowViewModel,
                async () =>
                {
                    var loggerFactory = new ObservableLoggerFactory();
                    var agentServices = await agentSessionShortcutContext
                        .CreateAgentServicesAsync(mainWindowViewModel, loggerFactory);
                    var agentManifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
                    // Populate the manifest's stable identity from the source entity so the
                    // ManifestIdentity consent scope works for real manifest entities (issue #1401).
                    agentManifest.Metadata ??= new Dictionary<string, object>();
                    if (!agentManifest.Metadata.ContainsKey(AgentManifestSecretUseMemoryFactory.EntityIdMetadataKey))
                    {
                        agentManifest.Metadata[AgentManifestSecretUseMemoryFactory.EntityIdMetadataKey] =
                            agentSourceEntity.EntityId.ToString();
                    }
                    var lease = await openAgentSessionShortcutHandler.RunningAgentChatTable.AcquireAsync(
                        new AcquireAgentChatRequest
                        {
                            AgentSessionId = new AgentSessionId(agentSessionId),
                            AgentSessionEntity = createdAgentSessionEntity.Data as JsonElement?,
                            AgentManifest = agentManifest,
                            Parameters = parameterValues,
                            AgentServices = agentServices,
                            ToolResourceFactory = agentServices.ToolResourceFactory,
                            ForegroundScheduler = foregroundScheduler,
                            EntityName = createdAgentSessionEntity.DisplayName,
                            EntityId = createdAgentSessionEntity.EntityId.ToString(),
                            WorkspaceId = loadingTab.WorkspacePaneId,
                        });
                    loadingTab.SetLease(lease);
                    return (lease.LocalAgentChat, loggerFactory);
                }, createdAgentSessionEntity, loadingTab, foregroundScheduler));
        }
        else if (data.TryGetProperty("definition", out var definitionElement))
        {
            var definitionJson = definitionElement.GetRawText();
            lifetime.Run(_ => InitializeSessionTabAsync(
                openAgentSessionShortcutHandler,
                mainWindowViewModel,
                async () =>
                {
                    var loggerFactory = new ObservableLoggerFactory();
                    var agentServices = await agentSessionShortcutContext
                        .CreateAgentServicesAsync(mainWindowViewModel, loggerFactory);
                    var agentDefinition = PhantomAgentSchema.AgentDefinitionFromJson(definitionJson);
                    var lease = await openAgentSessionShortcutHandler.RunningAgentChatTable.AcquireAsync(
                        new AcquireAgentChatRequest
                        {
                            AgentSessionId = new AgentSessionId(agentSessionId),
                            AgentSessionEntity = createdAgentSessionEntity.Data as JsonElement?,
                            AgentDefinition = agentDefinition,
                            AgentServices = agentServices,
                            ToolResourceFactory = agentServices.ToolResourceFactory,
                            ForegroundScheduler = foregroundScheduler,
                            EntityName = createdAgentSessionEntity.DisplayName,
                            EntityId = createdAgentSessionEntity.EntityId.ToString(),
                            WorkspaceId = loadingTab.WorkspacePaneId,
                        });
                    loadingTab.SetLease(lease);
                    return (lease.LocalAgentChat, loggerFactory);
                }, createdAgentSessionEntity, loadingTab, foregroundScheduler));
        }

        return createdAgentSessionEntity;
    }

    private static async Task<Phantom.Workspaces.Llm.Trust.TrustProfile?> ResolveSelectedTrustProfileAsync(
        MainWindowViewModel mainWindowViewModel,
        IReadOnlyDictionary<string, JsonElement>? parameterSelections)
    {
        if (parameterSelections is null)
        {
            return null;
        }

        foreach (var selection in parameterSelections.Values)
        {
            if (ExecutorParameterSelection.TryGetTrustProfile(selection, out var profileName)
                && !string.IsNullOrWhiteSpace(profileName))
            {
                var resolver = new DataAccessLayerTrustProfileResolver(
                    mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer);
                return await resolver.ResolveAsync(profileName);
            }
        }

        return null;
    }

    private static async Task InitializeSessionTabAsync(
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        MainWindowViewModel mainWindowViewModel,
        Func<Task<(AgentChat AgentChat, ObservableLoggerFactory LoggerFactory)>> createChatAsync,
        SubscribedEntityViewModel createdAgentSessionEntity,
        AgentSessionWorkspaceTabViewModel loadingTab,
        TaskScheduler foregroundScheduler)
    {
        try
        {
            var (agentChat, loggerFactory) = await createChatAsync();
            // #1429: materialize through the single composition seam so slash commands are always wired.
            var agent = openAgentSessionShortcutHandler.ComposeSessionAgentViewModel(
                mainWindowViewModel, loggerFactory, agentChat, createdAgentSessionEntity, loadingTab, foregroundScheduler);
            loadingTab.SetReady(agent, loggerFactory);
        }
        catch (Exception ex)
        {
            loadingTab.SetFailed(ex.Message);
        }
    }
}
