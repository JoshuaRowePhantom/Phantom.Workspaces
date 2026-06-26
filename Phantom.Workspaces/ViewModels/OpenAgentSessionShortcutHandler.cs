using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
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
        // Open a loading tab immediately so the user sees feedback right away.
        // OpenTabAsync dedupes by Id, so if the session is already open it just activates it.
        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = entityViewModel.EntityId.ToString(),
            Title = entityViewModel.DisplayName,
            DockRegion = "full",
            Entity = entityViewModel,
            TabHeader = new IconTabHeaderViewModel { Icon = "🧠", Title = entityViewModel.DisplayName },
            NotificationService = mainWindowViewModel.NotificationService,
        };
        await mainWindowViewModel.OpenTabAsync(loadingTab);

        // Complete initialization in the background
        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(() => InitializeTabInBackgroundAsync(mainWindowViewModel, entityViewModel, loadingTab, foregroundScheduler));

        return true;
    }

    private async Task InitializeTabInBackgroundAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentSessionWorkspaceTabViewModel tab,
        TaskScheduler foregroundScheduler)
    {
        try
        {
            var result = await this.TryBuildAgentAsync(mainWindowViewModel, agentSessionEntity, foregroundScheduler);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result is var (agent, loggerFactory))
                {
                    tab.SetReady(agent, loggerFactory);
                }
                else
                {
                    tab.SetFailed("Could not load agent session: missing required entity data.");
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => tab.SetFailed(ex.Message));
        }
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
        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = tabId ?? agentSessionEntity.EntityId.ToString(),
            Title = title ?? agentSessionEntity.DisplayName,
            DockRegion = dockRegion ?? "full",
            Entity = agentSessionEntity,
            TabHeader = new IconTabHeaderViewModel { Icon = "🧠", Title = title ?? agentSessionEntity.DisplayName },
            NotificationService = mainWindowViewModel.NotificationService,
        };

        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(() => InitializeTabInBackgroundAsync(mainWindowViewModel, agentSessionEntity, loadingTab, foregroundScheduler));

        return loadingTab;
    }

    public async Task<AgentSessionWorkspaceTabViewModel> CreateAgentSessionTabAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentChat agentChat)
    {
        var loggerFactory = new ObservableLoggerFactory();
        var agent = BuildAgentViewModel(mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity.DisplayName);
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = agentSessionEntity.EntityId.ToString(),
            Title = agentSessionEntity.DisplayName,
            DockRegion = "full",
            Entity = agentSessionEntity,
            TabHeader = new IconTabHeaderViewModel { Icon = "🧠", Title = agentSessionEntity.DisplayName },
            NotificationService = mainWindowViewModel.NotificationService,
        };
        tab.SetReady(agent, loggerFactory);
        return tab;
    }

    private async Task<(AgentViewModel agent, ObservableLoggerFactory loggerFactory)?> TryBuildAgentAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        TaskScheduler foregroundScheduler)
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
                ForegroundScheduler = foregroundScheduler,
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
                ForegroundScheduler = foregroundScheduler,
            };
        }
        else
        {
            return null;
        }

        var agentChat = await AgentFactory.CreateAgentChatAsync(createAgentChatRequest);
        var agent = BuildAgentViewModel(mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity.DisplayName);
        return (agent, loggerFactory);
    }

    public AgentViewModel BuildAgentViewModelPublic(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        string title)
    {
        return BuildAgentViewModel(mainWindowViewModel, loggerFactory, agentChat, title);
    }

    private static AgentViewModel BuildAgentViewModel(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        string title)
    {
        return new AgentViewModel(agentChat, title, loggerFactory)
        {
            OpenUrlHandler = url => _ = mainWindowViewModel.OpenTabAsync(
                new WebViewModel(url, mainWindowViewModel)
                {
                    Id = $"web-{url}",
                    Title = url,
                }),
        };
    }
}
