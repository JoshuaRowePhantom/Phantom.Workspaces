using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

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
            || (!agentSessionEntityData.TryGetProperty("agent-source-entity-id", out var agentDefinitionEntityIdElement)
                && !agentSessionEntityData.TryGetProperty("agent-definition-entity-id", out agentDefinitionEntityIdElement))
            || agentDefinitionEntityIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentDefinitionEntityIdElement.GetString())
            || !Guid.TryParse(agentDefinitionEntityIdElement.GetString(), out var agentDefinitionEntityIdValue))
        {
            return null;
        }

        var agentSessionId = agentSessionIdElement.GetString();
        var agentDefinitionEntityId = new EntityId(agentDefinitionEntityIdValue);
        var parameterValues = agentSessionEntityData.TryGetProperty("parameter-values", out var pvElement)
            ? ReadStringDictionary(pvElement)
            : null;
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
                Parameters = parameterValues,
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

        agent.ConfigureSlashCommands(
            () => new SlashCommandContext
            {
                AgentChat = agentChat,
                AgentSessionEntityId = agentSessionEntity.EntityId.ToString(),
                CurrentParameterValues = ReadStringDictionary(
                    agentSessionEntity.Data is JsonElement d
                    && d.TryGetProperty("parameter-values", out var pv) ? pv : default),
                UpdateParameterValuesAsync = (newValues, ct) =>
                    UpdateParameterValuesInEntityAsync(mainWindowViewModel, agentSessionEntity, newValues),
            });

        var profileEntityId = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        if (profileEntityId != default)
        {
            var profileEntities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([profileEntityId]);
            var profileEntity = profileEntities.FirstOrDefault();
            if (profileEntity?.Data is JsonElement profileData)
            {
                if (profileData.TryGetProperty("chat-input", out var chatInputEl)
                    && chatInputEl.TryGetProperty("show-help-text", out var showHelpEl)
                    && showHelpEl.ValueKind == JsonValueKind.False)
                {
                    agent.ShowChatInputHelpText = false;
                }
            }

            agent.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(AgentViewModel.ShowChatInputHelpText)
                    && sender is AgentViewModel vm)
                {
                    _ = SaveChatInputHelpTextAsync(mainWindowViewModel, profileEntityId, vm.ShowChatInputHelpText);
                }
            };
        }

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

    private static async Task UpdateParameterValuesInEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        IReadOnlyDictionary<string, string> newValues)
    {
        if (agentSessionEntity.Data is not JsonElement currentData)
        {
            return;
        }

        var mergedJson = MergeParameterValues(currentData, newValues);
        using var mergedDoc = JsonDocument.Parse(mergedJson);
        await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Update parameter-values for {agentSessionEntity.DisplayName}.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = mergedDoc.RootElement.Clone(),
                    },
                ],
            });
    }

    private static string MergeParameterValues(
        JsonElement entityData,
        IReadOnlyDictionary<string, string> newValues)
    {
        var paramValuesJson = JsonSerializer.Serialize(newValues);
        var writer = new StringBuilder();
        writer.Append('{');
        var first = true;
        var hadParameterValues = false;
        foreach (var prop in entityData.EnumerateObject())
        {
            if (!first)
            {
                writer.Append(',');
            }
            first = false;
            if (prop.Name == "parameter-values")
            {
                hadParameterValues = true;
                writer.Append($"\"parameter-values\":{paramValuesJson}");
            }
            else
            {
                writer.Append($"\"{prop.Name}\":{prop.Value.GetRawText()}");
            }
        }
        if (!hadParameterValues)
        {
            if (!first)
            {
                writer.Append(',');
            }
            writer.Append($"\"parameter-values\":{paramValuesJson}");
        }
        writer.Append('}');
        return writer.ToString();
    }

    private static IReadOnlyDictionary<string, string>? ReadStringDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                dict[prop.Name] = prop.Value.GetString()!;
            }
        }
        return dict.Count > 0 ? dict : null;
    }

    private static async Task SaveChatInputHelpTextAsync(
        MainWindowViewModel mainWindowViewModel,
        EntityId profileEntityId,
        bool showHelpText)
    {
        var entities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([profileEntityId]);
        var entity = entities.FirstOrDefault();
        if (entity?.Data is not JsonElement currentData)
        {
            return;
        }

        var node = JsonNode.Parse(currentData.GetRawText())!.AsObject();
        var chatInput = node["chat-input"]?.AsObject() ?? new JsonObject();
        chatInput["show-help-text"] = showHelpText;
        node["chat-input"] = chatInput;
        var updated = JsonSerializer.SerializeToElement(node);

        await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "chat-input: set show-help-text",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = profileEntityId,
                        Data = updated,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
    }
}
