using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAgentSessionShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;

    public OpenAgentSessionShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
    }

    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Open
            && entityViewModel.IsEntityType("agent-session");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement agentSessionEntityData
            || !agentSessionEntityData.TryGetProperty("agent-session-id", out var agentSessionIdElement)
            || agentSessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentSessionIdElement.GetString())
            || !agentSessionEntityData.TryGetProperty("agent-definition-entity-id", out var agentDefinitionEntityIdElement)
            || agentDefinitionEntityIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentDefinitionEntityIdElement.GetString())
            || !Guid.TryParse(agentDefinitionEntityIdElement.GetString(), out var agentDefinitionEntityIdValue))
        {
            return false;
        }

        var agentSessionId = agentSessionIdElement.GetString();
        var agentDefinitionEntityId = new EntityId(agentDefinitionEntityIdValue);
        var agentDefinitionEntity = (await mainWindowViewModel.EntityBroker.GetEntitiesAsync([agentDefinitionEntityId]))
            .FirstOrDefault();
        if (agentDefinitionEntity?.Data is not JsonElement agentSourceEntityData)
        {
            return false;
        }

        var loggerFactory = new ObservableLoggerFactory();
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel, loggerFactory);

        CreateAgentChatRequest createAgentChatRequest;
        if (agentSourceEntityData.TryGetProperty("definition", out var definitionElement))
        {
            createAgentChatRequest = new CreateAgentChatRequest
            {
                AgentDefinition = AgentDefinition.FromJson(definitionElement.GetRawText()),
                AgentSessionId = agentSessionId,
                AgentServices = agentServices,
            };
        }
        else if (agentSourceEntityData.TryGetProperty("manifest", out var manifestElement))
        {
            createAgentChatRequest = new CreateAgentChatRequest
            {
                AgentManifest = AgentManifestLoader.LoadManifestFromJson(manifestElement.GetRawText()),
                ToolResourceFactory = agentServices.ToolResourceFactory,
                AgentSessionId = agentSessionId,
                AgentServices = agentServices,
            };
        }
        else
        {
            return false;
        }

        var agentChat = await AgentFactory.CreateAgentChatAsync(createAgentChatRequest);

        var workspaceTab = CreateAgentSessionTab(entityViewModel, loggerFactory, agentChat);
        await mainWindowViewModel.OpenTabAsync(workspaceTab);
        return true;
    }

    public async Task<AgentSessionWorkspaceTabViewModel> CreateAgentSessionTabAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentChat agentChat)
    {
        var loggerFactory = new ObservableLoggerFactory();
        return CreateAgentSessionTab(agentSessionEntity, loggerFactory, agentChat);
    }

    private static AgentSessionWorkspaceTabViewModel CreateAgentSessionTab(
        SubscribedEntityViewModel agentSessionEntity,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat)
    {
        return new AgentSessionWorkspaceTabViewModel
        {
            Id = agentSessionEntity.EntityId.ToString(),
            Title = agentSessionEntity.DisplayName,
            Entity = agentSessionEntity,
            LoggerFactory = loggerFactory,
            Agent = new Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel(agentChat, agentSessionEntity.DisplayName, loggerFactory),
        };
    }
}

