using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.ViewModels;

public sealed class StartAgentSessionOnProfileShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public StartAgentSessionOnProfileShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
    }

    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.StartAgentSession
            && entityViewModel.IsEntityType("user-computer-profile");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        // For now, look for a default agent definition by name
        // TODO: Show UI to select from available agent definitions
        var getRequest = new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName(["agent-definitions", "defaults", "github-models"]),
                },
            ],
        };

        var loadedEntities = await mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer.GetAsync(getRequest);
        var firstAgentDefinitionSnapshot = loadedEntities.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault();
        if (firstAgentDefinitionSnapshot is null)
        {
            // No default agent definition found
            return false;
        }

        var agentDefinitionEntities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([firstAgentDefinitionSnapshot.EntityId]);
        var firstAgentDefinition = agentDefinitionEntities.FirstOrDefault();
        if (firstAgentDefinition is null)
        {
            return false;
        }

        // Get the agent definition data
        if (firstAgentDefinition.Data is not JsonElement agentDefinitionEntityData
            || !agentDefinitionEntityData.TryGetProperty("definition", out var definitionElement))
        {
            return false;
        }

        var agentDefinition = AgentDefinition.FromJson(definitionElement.GetRawText());
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel);
        
        // Create agent chat with the user-computer-profile as host
        var agentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentServices = agentServices,
            });

        try
        {
            // Create agent session entity with host reference to the user-computer-profile
            var createdAgentSessionEntity = await this.CreateAgentSessionEntityWithHostAsync(
                mainWindowViewModel,
                firstAgentDefinition,
                entityViewModel,
                agentChat.AgentSessionId);
            
            if (createdAgentSessionEntity is null)
            {
                return false;
            }

            return await this.openAgentSessionShortcutHandler.Handle(mainWindowViewModel, Shortcut.Open, createdAgentSessionEntity);
        }
        finally
        {
            await agentChat.DisposeAsync();
        }
    }

    private async Task<SubscribedEntityViewModel?> CreateAgentSessionEntityWithHostAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentDefinitionEntity,
        SubscribedEntityViewModel hostProfileEntity,
        string agentSessionId)
    {
        var workspaceEntitySession = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession;
        var currentTime = DateTimeOffset.UtcNow;
        var sessionObjectSimpleName = CreateSessionObjectSimpleName(agentSessionId, currentTime);
        
        var agentSessionNames = await WorkspaceEntityNameFactory.CreateEntityNames(
            mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("agent-session"),
            sessionObjectSimpleName);
        
        var agentSessionEntityData = CreateAgentSessionEntityDataWithHost(
            agentDefinitionEntity.EntityId,
            agentDefinitionEntity.DisplayName,
            hostProfileEntity.EntityId,
            agentSessionId,
            agentSessionNames);
        
        var createAgentSessionResult = await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Create agent-session entity for {agentDefinitionEntity.DisplayName} on {hostProfileEntity.DisplayName}.",
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

    private static JsonElement CreateAgentSessionEntityDataWithHost(
        EntityId agentDefinitionEntityId,
        string agentDisplayName,
        EntityId hostProfileEntityId,
        string agentSessionId,
        System.Collections.Generic.IReadOnlyCollection<EntityName> agentSessionNames)
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
              "agent-session-id": "{{agentSessionId}}",
              "host-profile-entity-id": "{{hostProfileEntityId}}"
            }
            """);
        return agentSessionDocument.RootElement.Clone();
    }

    private static string CreateSessionObjectSimpleName(
        string agentSessionId,
        DateTimeOffset currentTime)
    {
        var timestampComponent = currentTime.ToString("yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        return $"session-{timestampComponent}-{agentSessionId}";
    }
}
