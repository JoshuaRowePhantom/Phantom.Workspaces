using System;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Phantom.Workspaces.Agent.Gui;
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

        // Pre-generate the agent session ID so the entity can be created before the slow agent init.
        var agentSessionId = Guid.NewGuid().ToString("n");

        // Create the agent-session entity first (fast data write) to get its entity ID and reference.
        var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            mainWindowViewModel, entityViewModel, agentSessionId);
        if (createdAgentSessionEntity is null)
        {
            return false;
        }

        // Open a loading tab immediately so the user sees feedback right away.
        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = createdAgentSessionEntity.EntityId.ToString(),
            Title = createdAgentSessionEntity.DisplayName,
            DockRegion = "full",
            Entity = createdAgentSessionEntity,
            TabHeader = new IconTabHeaderViewModel { Icon = "🧠", Title = createdAgentSessionEntity.DisplayName },
        };
        await mainWindowViewModel.OpenTabAsync(loadingTab);

        // Initialize the agent chat in the background.
        var definitionJson = definitionElement.GetRawText();
        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(async () =>
        {
            try
            {
                var loggerFactory = new ObservableLoggerFactory();
                var agentServices = await this.agentSessionShortcutContext
                    .CreateAgentServicesAsync(mainWindowViewModel, loggerFactory);
                var agentDefinition = AgentDefinition.FromJson(definitionJson);
                var agentChat = await AgentFactory.CreateAgentChatAsync(
                    new CreateAgentChatRequest
                    {
                        AgentDefinition = agentDefinition,
                        AgentSessionId = agentSessionId,
                        AgentServices = agentServices,
                        ForegroundScheduler = foregroundScheduler,
                    });
                var agent = this.openAgentSessionShortcutHandler.BuildAgentViewModelPublic(
                    mainWindowViewModel, loggerFactory, agentChat, createdAgentSessionEntity.DisplayName);
                await Dispatcher.UIThread.InvokeAsync(() => loadingTab.SetReady(agent, loggerFactory));
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => loadingTab.SetFailed(ex.Message));
            }
        });

        return true;
    }
}
