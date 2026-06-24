using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAgentManifestShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;

    public OpenAgentManifestShortcutHandler(
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
            && entityViewModel.IsEntityType("agent-manifest");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement agentManifestEntityData
            || !agentManifestEntityData.TryGetProperty("manifest", out var manifestElement))
        {
            return false;
        }

        var agentManifest = AgentManifestLoader.LoadManifestFromJson(manifestElement.GetRawText());
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel);
        var agentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentManifest = agentManifest,
                ToolResourceFactory = agentServices.ToolResourceFactory,
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
