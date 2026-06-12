using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentSessionShortcutContext
{
    private const string AgentSessionCollectionSuffix = "-agent-sessions";
    private Task<IAgentPersistenceStore>? agentPersistenceStoreTask;

    public async Task<AgentServices> CreateAgentServicesAsync(
        MainWindowViewModel mainWindowViewModel)
    {
        var agentPersistenceStore = await this.GetAgentPersistenceStoreAsync(mainWindowViewModel);
        return new AgentServices
        {
            AgentPersistenceStoreOverride = agentPersistenceStore,
            ToolsetFactory = ToolsetFactory.CreateDefaultToolsetFactory(),
        };
    }

    public async Task<SubscribedEntityViewModel?> CreateAgentSessionEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentDefinitionEntity,
        string agentSessionId)
    {
        var workspaceEntitySession = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession;
        var agentSessionNames = await WorkspaceEntityNameFactory.CreateEntityNames(
            mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("agent-session"),
            agentSessionId);
        var agentSessionEntityData = CreateAgentSessionEntityData(
            agentDefinitionEntity.EntityId,
            agentDefinitionEntity.DisplayName,
            agentSessionId,
            agentSessionNames);
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
        if (this.agentPersistenceStoreTask is not null)
        {
            return this.agentPersistenceStoreTask;
        }

        this.agentPersistenceStoreTask = CreateAgentPersistenceStoreAsync(mainWindowViewModel.RepositorySource);
        return this.agentPersistenceStoreTask;
    }

    private static async Task<IAgentPersistenceStore> CreateAgentPersistenceStoreAsync(
        RepositorySource repositorySource)
    {
        if (repositorySource.SourceType != RepositorySourceType.MongoDb
            || string.IsNullOrWhiteSpace(repositorySource.MongoDbContainerName)
            || string.IsNullOrWhiteSpace(repositorySource.MongoDbRootCollectionName))
        {
            return AgentPersistenceStoreFactory.CreateInMemory();
        }

        var mongoDbDataDirectory = string.IsNullOrWhiteSpace(repositorySource.MongoDbDataDirectory)
            ? Path.GetFullPath(".\\mongo-data")
            : repositorySource.MongoDbDataDirectory;
        var mongoDbDatabaseName = string.IsNullOrWhiteSpace(repositorySource.MongoDbDatabaseName)
            ? "phantom-workspaces"
            : repositorySource.MongoDbDatabaseName;
        var agentSessionCollectionName = $"{repositorySource.MongoDbRootCollectionName}{AgentSessionCollectionSuffix}";
        var chatHistoryProviderDefinition = ChatHistoryProviderDefinition.CreateMongoDb(
            provider: "container",
            databaseName: mongoDbDatabaseName,
            collectionName: agentSessionCollectionName,
            containerName: repositorySource.MongoDbContainerName,
            dataDirectory: mongoDbDataDirectory,
            hostPort: repositorySource.MongoDbHostPort);
        return await AgentPersistenceStoreFactory.CreateAsync(chatHistoryProviderDefinition);
    }

    private static JsonElement CreateAgentSessionEntityData(
        EntityId agentDefinitionEntityId,
        string agentDisplayName,
        string agentSessionId,
        IReadOnlyCollection<EntityName> agentSessionNames)
    {
        var entityId = new EntityId();
        var namesJson = string.Join(
            ", ",
            agentSessionNames.Select(
                static entityName => $"[{string.Join(", ", entityName.Components.Select(static component => JsonSerializer.Serialize(component)))}]"));
        using var agentSessionDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["agent-session"],
              "names": [{{namesJson}}],
              "display-name": { "default": "{{agentDisplayName}} session" },
              "agent-definition-entity-id": "{{agentDefinitionEntityId}}",
              "agent-session-id": "{{agentSessionId}}"
            }
            """);
        return agentSessionDocument.RootElement.Clone();
    }
}
