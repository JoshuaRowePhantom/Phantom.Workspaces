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
        var workspaceTab = await this.TryCreateAgentSessionTabForRestoreAsync(
            mainWindowViewModel, entityViewModel);
        if (workspaceTab is null)
        {
            return false;
        }

        await mainWindowViewModel.OpenTabAsync(workspaceTab);
        return true;
    }

    /// <summary>
    /// Creates an <see cref="AgentSessionWorkspaceTabViewModel"/> for the given
    /// <paramref name="agentSessionEntity"/> without opening it as a tab.
    /// Returns <see langword="null"/> if the entity data is missing required fields or the
    /// referenced agent-definition entity cannot be found.
    /// </summary>
    public async Task<AgentSessionWorkspaceTabViewModel?> TryCreateAgentSessionTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        string? tabId = null,
        string? title = null,
        string? dockRegion = null)
    {
        if (agentSessionEntity.Data is not JsonElement agentSessionEntityData
            || !agentSessionEntityData.TryGetProperty("agent-session-id", out var agentSessionIdElement)
            || agentSessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentSessionIdElement.GetString())
            || !agentSessionEntityData.TryGetProperty("agent-definition-entity-id", out var agentDefinitionEntityIdElement)
            || agentDefinitionEntityIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentDefinitionEntityIdElement.GetString())
            || !Guid.TryParse(agentDefinitionEntityIdElement.GetString(), out var agentDefinitionEntityIdValue))
        {
            return null;
        }

        var agentSessionId = agentSessionIdElement.GetString();
        var agentDefinitionEntityId = new EntityId(agentDefinitionEntityIdValue);
        var agentDefinitionEntity = (await mainWindowViewModel.EntityBroker.GetEntitiesAsync([agentDefinitionEntityId]))
            .FirstOrDefault();
        if (agentDefinitionEntity?.Data is not JsonElement agentSourceEntityData)
        {
            return null;
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
            return null;
        }

        var agentChat = await AgentFactory.CreateAgentChatAsync(createAgentChatRequest);
        return CreateAgentSessionTab(
            mainWindowViewModel, agentSessionEntity, loggerFactory, agentChat,
            tabId: tabId ?? agentSessionEntity.EntityId.ToString(),
            title: title ?? agentSessionEntity.DisplayName,
            dockRegion: dockRegion ?? "full");
    }

    public async Task<AgentSessionWorkspaceTabViewModel> CreateAgentSessionTabAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentChat agentChat)
    {
        var loggerFactory = new ObservableLoggerFactory();
        return CreateAgentSessionTab(
            mainWindowViewModel, agentSessionEntity, loggerFactory, agentChat,
            tabId: agentSessionEntity.EntityId.ToString(),
            title: agentSessionEntity.DisplayName,
            dockRegion: "full");
    }

    private static AgentSessionWorkspaceTabViewModel CreateAgentSessionTab(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        string tabId,
        string title,
        string dockRegion)
    {
        var agent = new Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel(agentChat, title, loggerFactory)
        {
            OpenUrlHandler = url => _ = mainWindowViewModel.OpenTabAsync(
                new WebViewModel(url, mainWindowViewModel)
                {
                    Id = $"web-{url}",
                    Title = url,
                }),
        };

        return new AgentSessionWorkspaceTabViewModel
        {
            Id = tabId,
            Title = title,
            DockRegion = dockRegion,
            Entity = agentSessionEntity,
            LoggerFactory = loggerFactory,
            Agent = agent,
        };
    }
}
