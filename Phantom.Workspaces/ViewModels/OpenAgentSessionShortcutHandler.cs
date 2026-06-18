using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

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
        if (agentDefinitionEntity?.Data is not JsonElement agentDefinitionEntityData
            || !agentDefinitionEntityData.TryGetProperty("definition", out var definitionElement))
        {
            return false;
        }

        var agentDefinition = AgentDefinition.FromJson(definitionElement.GetRawText());
        var loggerFactory = new ObservableLoggerFactory();
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel, loggerFactory);
        var agentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentSessionId = agentSessionId,
                AgentServices = agentServices,
            });

        var workspaceTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = entityViewModel.EntityId.ToString(),
            Title = entityViewModel.DisplayName,
            Entity = entityViewModel,
            LoggerFactory = loggerFactory,
            Agent = new Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel(agentChat, entityViewModel.DisplayName, loggerFactory),
        };
        await mainWindowViewModel.OpenTabAsync(workspaceTab);
        return true;
    }
}

