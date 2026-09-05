using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Thin GUI adapter for launching agent sessions (issue #1403). Resolves the small GUI-specific
/// context and delegates the heavy lifting to the proper layers: the shared
/// <see cref="AgentServicesComposition"/> for the complete <see cref="AgentServices"/> bundle, the
/// <see cref="AgentPersistenceStoreSourceFactory"/> for the persistence store, and the data-layer
/// <see cref="AgentSessionEntityFactory"/> for authoring the agent-session entity document. This
/// class no longer owns AgentServices composition, MCP tool-resource wiring, the
/// <c>RepositorySource</c> persistence switch, or entity JSON authoring.
/// </summary>
public sealed class AgentSessionShortcutContext
{
    private readonly TimeProvider timeProvider;
    private readonly string? userComputerProfileOverride;
    private readonly IAgentPersistenceStoreCache? persistenceStoreCache;
    private Task<IAgentPersistenceStore>? agentPersistenceStoreTask;

    public AgentSessionShortcutContext(
        TimeProvider? timeProvider = null,
        string? userComputerProfileOverride = null,
        IAgentPersistenceStoreCache? persistenceStoreCache = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.userComputerProfileOverride = userComputerProfileOverride;
        this.persistenceStoreCache = persistenceStoreCache;
    }

    public async Task<AgentServices> CreateAgentServicesAsync(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory? loggerFactory = null)
    {
        var agentPersistenceStore = await this.GetAgentPersistenceStoreAsync(mainWindowViewModel);
        return await AgentServicesComposition.ComposeSessionServicesAsync(
            mainWindowViewModel,
            agentPersistenceStore,
            this.userComputerProfileOverride,
            loggerFactory);
    }

    public async Task<SubscribedEntityViewModel?> CreateAgentSessionEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentDefinitionEntity,
        string agentSessionId,
        IReadOnlyDictionary<string, string>? parameterValues = null,
        IReadOnlyDictionary<string, JsonElement>? parameterSelections = null,
        EntityId? hostProfileEntityId = null)
    {
        var workspaceEntitySession = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession;
        var executionContext = new CurrentExecutionContextProvider(this.userComputerProfileOverride);
        var computerName = executionContext.EffectiveComputerName;
        var currentTime = this.timeProvider.GetUtcNow();
        var sessionObjectSimpleName = AgentSessionEntityFactory.CreateSessionSimpleName(
            agentSessionId,
            currentTime,
            computerName);
        var agentSessionNames = await WorkspaceEntityNameFactory.CreateEntityNames(
            mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("agent-session"),
            sessionObjectSimpleName);
        var agentSessionEntityData = AgentSessionEntityFactory.CreateEntityData(
            agentDefinitionEntity.EntityId,
            agentDefinitionEntity.DisplayName,
            agentSessionId,
            agentSessionNames,
            currentTime,
            computerName,
            parameterValues,
            hostProfileEntityId,
            parameterSelections: parameterSelections);
        var createAgentSessionResult = await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Create agent-session entity for {agentDefinitionEntity.DisplayName}.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        Data = agentSessionEntityData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
        var createAgentSessionEntityResult = createAgentSessionResult.EntityResults
            .FirstOrDefault(entityResult => entityResult.UpdateState != UpdateState.Failed && entityResult.CurrentEntity is not null);
        if (createAgentSessionEntityResult?.CurrentEntity is not EntitySnapshot createdAgentSessionSnapshot)
        {
            return null;
        }

        var createdAgentSessionEntities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([createdAgentSessionSnapshot.EntityId]);
        return createdAgentSessionEntities.FirstOrDefault();
    }

    private Task<IAgentPersistenceStore> GetAgentPersistenceStoreAsync(
        MainWindowViewModel mainWindowViewModel)
    {
        if (this.persistenceStoreCache is not null)
        {
            return this.persistenceStoreCache.GetOrCreateAsync(mainWindowViewModel.RepositorySource);
        }

        if (this.agentPersistenceStoreTask is not null)
        {
            return this.agentPersistenceStoreTask;
        }

        this.agentPersistenceStoreTask = AgentPersistenceStoreSourceFactory.CreateForRepositorySourceAsync(
            mainWindowViewModel.RepositorySource);
        return this.agentPersistenceStoreTask;
    }
}
