using System;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAgentDefinitionShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public OpenAgentDefinitionShortcutHandler(
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
        return shortcut == Shortcut.Open
            && entityViewModel.IsEntityType("agent-definition");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement agentDefinitionEntityData
            || !agentDefinitionEntityData.TryGetProperty("definition", out var definitionElement))
        {
            return false;
        }

        var agentDefinition = AgentDefinition.FromJson(definitionElement.GetRawText());
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel);
        var agentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentServices = agentServices,
            });

        try
        {
            var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
                mainWindowViewModel,
                entityViewModel,
                agentChat.AgentSessionId);
            if (createdAgentSessionEntity is null)
            {
                return false;
            }

            return await this.openAgentSessionShortcutHandler.Handle(mainWindowViewModel, shortcut, createdAgentSessionEntity);
        }
        finally
        {
            await agentChat.DisposeAsync();
        }
    }
}
