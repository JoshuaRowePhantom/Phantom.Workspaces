using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentSessionShortcutContext
{
    private const string AgentSessionCollectionSuffix = "-agent-sessions";
    private readonly Func<DateTimeOffset> currentTimeProvider;
    private readonly string? userComputerProfileOverride;
    private Task<IAgentPersistenceStore>? agentPersistenceStoreTask;

    public AgentSessionShortcutContext(
        Func<DateTimeOffset>? currentTimeProvider = null,
        string? userComputerProfileOverride = null)
    {
        this.currentTimeProvider = currentTimeProvider
            ?? (() => DateTimeOffset.UtcNow);
        this.userComputerProfileOverride = userComputerProfileOverride;
    }

    public async Task<AgentServices> CreateAgentServicesAsync(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory? loggerFactory = null)
    {
        var agentPersistenceStore = await this.GetAgentPersistenceStoreAsync(mainWindowViewModel);
        var dataAccessLayer = mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;
        var workspaceGuiContextProvider = new WorkspaceGuiContextProvider(
            new WorkspaceGuiContext
            {
                MainWindowViewModel = mainWindowViewModel,
                ShortcutManager = mainWindowViewModel.ShortcutManager,
            });
        var toolsetFactory = ToolsetFactory.CreateWorkspaceEntityToolsetFactory(
            dataAccessLayer,
            ToolsetFactory.CreateWorkspaceGuiToolsetFactory(
                workspaceGuiContextProvider,
                ToolsetFactory.CreateDefaultToolsetFactory()));
        return new AgentServices
        {
            AgentPersistenceStoreOverride = agentPersistenceStore,
            LoggerFactory = loggerFactory,
            ToolsetFactory = toolsetFactory,
            ToolResourceFactory = this.CreateToolResourceFactory(dataAccessLayer),
        };
    }

    private IToolResourceFactory CreateToolResourceFactory(IDataAccessLayer dataAccessLayer)
    {
        var executionContext = new CurrentExecutionContextProvider(this.userComputerProfileOverride);
        var machineProfilePrefix = new EntityName(
            "computer-user-profiles",
            "users",
            "username",
            executionContext.UserName,
            "computers",
            "hostname",
            executionContext.EffectiveComputerName,
            "copilot",
            "mcp-servers");

        return new ComposingToolResourceFactory(
            new FixedToolResourceFactory(CreateFixedToolMapping()),
            new McpServerEntityToolResourceFactory(
                dataAccessLayer,
                [
                    machineProfilePrefix,
                    new EntityName("defaults", "mcp-servers"),
                ]));
    }

    private static IReadOnlyDictionary<(string Id, string Name), Tool> CreateFixedToolMapping()
    {
        return FixedToolResources.DefaultNames.ToDictionary(
            name => (FixedToolResources.FixedToolResourceId, name),
            name => (Tool)new CustomTool { Kind = name, Name = name });
    }

    public async Task<SubscribedEntityViewModel?> CreateAgentSessionEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentDefinitionEntity,
        string agentSessionId)
    {
        var workspaceEntitySession = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession;
        var sessionObjectSimpleName = CreateSessionObjectSimpleName(
            agentSessionId,
            this.currentTimeProvider());
        var agentSessionNames = await WorkspaceEntityNameFactory.CreateEntityNames(
            mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("agent-session"),
            sessionObjectSimpleName);
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
        if (repositorySource is not MongoDbRepositorySource mongoSource
            || string.IsNullOrWhiteSpace(mongoSource.ContainerName)
            || string.IsNullOrWhiteSpace(mongoSource.RootCollectionName))
        {
            return AgentPersistenceStoreFactory.CreateInMemory();
        }

        var mongoDbDataDirectory = mongoSource.DataDirectory ?? string.Empty;
        var mongoDbDatabaseName = string.IsNullOrWhiteSpace(mongoSource.DatabaseName)
            ? "phantom-workspaces"
            : mongoSource.DatabaseName;
        var agentSessionCollectionName = $"{mongoSource.RootCollectionName}{AgentSessionCollectionSuffix}";
        var chatHistoryProviderDefinition = ChatHistoryProviderDefinition.CreateMongoDb(
            provider: "container",
            databaseName: mongoDbDatabaseName,
            collectionName: agentSessionCollectionName,
            containerName: mongoSource.ContainerName,
            dataDirectory: mongoDbDataDirectory,
            hostPort: mongoSource.HostPort);
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
              "entity-types": ["entity", "agent-session"],
              "names": [{{namesJson}}],
              "display-name": { "default": "{{agentDisplayName}} session" },
              "agent-definition-entity-id": "{{agentDefinitionEntityId}}",
              "agent-session-id": "{{agentSessionId}}"
            }
            """);
        return agentSessionDocument.RootElement.Clone();
    }

    private static string CreateSessionObjectSimpleName(
        string agentSessionId,
        DateTimeOffset currentTime)
    {
        var timestampComponent = currentTime.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
        return $"session-{timestampComponent}-{agentSessionId}";
    }
}
