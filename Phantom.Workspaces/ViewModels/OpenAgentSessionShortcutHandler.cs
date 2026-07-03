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
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAgentSessionShortcutHandler : ShortcutHandler
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly ITrustedExecutorSelector trustedExecutorSelector;
    private readonly IRunningAgentChatTable? runningAgentChatTable;

    public OpenAgentSessionShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext,
        ITrustedExecutorSelector trustedExecutorSelector,
        IRunningAgentChatTable? runningAgentChatTable = null)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.trustedExecutorSelector = trustedExecutorSelector;
        this.runningAgentChatTable = runningAgentChatTable;
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
        // Use a pane-scoped ID so OpenTabAsync deduplicates within the same workspace pane
        // while still allowing the same session to be open in multiple panes simultaneously,
        // sharing a single AgentChat via RunningAgentChatTable.
        var paneId = mainWindowViewModel.SelectedWorkspacePane?.Id;

        string? agentSessionId = null;
        if (entityViewModel.Data is System.Text.Json.JsonElement entityDataElement
            && entityDataElement.TryGetProperty("agent-session-id", out var agentSidEl)
            && agentSidEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            agentSessionId = agentSidEl.GetString();
        }

        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = paneId is not null ? $"{paneId}-{entityViewModel.EntityId}" : entityViewModel.EntityId.ToString(),
            Title = entityViewModel.DisplayName,
            DockRegion = "full",
            Entity = entityViewModel,
            NotificationService = mainWindowViewModel.NotificationService,
            AgentSessionId = agentSessionId,
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
            var result = await this.TryBuildAgentAsync(mainWindowViewModel, agentSessionEntity, tab.Id, foregroundScheduler);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result is var (agent, loggerFactory, lease))
                {
                    if (lease is not null)
                    {
                        tab.SetLease(lease);
                    }
                    tab.SetReady(agent, loggerFactory);
                }
                else
                {
                    tab.SetFailed("Could not load agent session: missing required entity data.");
                }

                mainWindowViewModel.NotifyAgentTabStateChanged();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                tab.SetFailed(ex.Message);
                mainWindowViewModel.NotifyAgentTabStateChanged();
            });
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

    /// <summary>
    /// Creates an agent chat for the given session in the <see cref="IRunningAgentChatTable"/>,
    /// enqueues <paramref name="resumePrompt"/> as the first user message, and returns the
    /// acquired lease. Returns <see langword="null"/> when the table is unavailable or the
    /// entity data is missing required fields.
    /// </summary>
    internal async Task<RunningAgentChatLease?> TryStartAutoResumeAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        string resumePrompt,
        TaskScheduler foregroundScheduler)
    {
        if (this.runningAgentChatTable is null)
        {
            return null;
        }

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

        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel);

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

        var lease = await this.runningAgentChatTable.AcquireAsync(
            new AgentSessionId(agentSessionId!));

        lease.AgentChat.EnqueueUserMessage(resumePrompt);
        return lease;
    }

    public async Task<AgentSessionWorkspaceTabViewModel> CreateAgentSessionTabAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentChat agentChat)
    {
        var loggerFactory = new ObservableLoggerFactory();
        var agent = BuildAgentViewModel(mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity.DisplayName, agentSessionEntity.EntityId.ToString());
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

    private async Task<(AgentViewModel agent, ObservableLoggerFactory loggerFactory, RunningAgentChatLease? lease)?> TryBuildAgentAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        string agentSessionTabId,
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

        AgentChat agentChat;
        RunningAgentChatLease? lease = null;

        if (this.runningAgentChatTable is not null)
        {
            lease = await this.runningAgentChatTable.AcquireAsync(
                new AgentSessionId(agentSessionId!),
                createAgentChatRequest.AgentDefinition,
                createAgentChatRequest.AgentServices,
                agentSessionEntity.DisplayName,
                agentSessionEntity.EntityId.ToString());
            agentChat = lease.AgentChat;
        }
        else
        {
            agentChat = await this.CreateAgentChatAsync(createAgentChatRequest, agentSessionEntityData, mainWindowViewModel);
        }

        var agent = BuildAgentViewModel(mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity.DisplayName, agentSessionTabId);

        var localProfileEntityId = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var owningProfileEntityId = ReadOwningProfileEntityId(agentSessionEntityData);
        var trustedExecutorIdentifier = createAgentChatRequest.AgentDefinition is not null
            && owningProfileEntityId != default
            && owningProfileEntityId != localProfileEntityId
            ? owningProfileEntityId.ToString()
            : TrustProfile.LocalClientInstance;

        agent.ConfigureSlashCommands(
            () => new SlashCommandContext
            {
                AgentChat = agentChat,
                AgentSessionEntityId = agentSessionEntity.EntityId.ToString(),
                TrustedExecutorIdentifier = trustedExecutorIdentifier,
                CurrentAutoResume = agentSessionEntity.Data is JsonElement entityDataSnapshot
                    ? AutoResumeService.ReadFromEntityData(entityDataSnapshot)
                    : null,
                UpdateAutoResumeAsync = (newSettings, ct) =>
                    UpdateAutoResumeInEntityAsync(mainWindowViewModel, agentSessionEntity, newSettings),
                CurrentParameterValues = ReadStringDictionary(
                    agentSessionEntity.Data is JsonElement d
                    && d.TryGetProperty("parameter-values", out var pv) ? pv : default),
                UpdateParameterValuesAsync = (newValues, ct) =>
                {
                    agentChat.UpdateParameterValues(newValues);
                    return UpdateParameterValuesInEntityAsync(mainWindowViewModel, agentSessionEntity, newValues);
                },
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

        return (agent, loggerFactory, lease);
    }

    private async Task<AgentChat> CreateAgentChatAsync(
        CreateAgentChatRequest createAgentChatRequest,
        JsonElement agentSessionEntityData,
        MainWindowViewModel mainWindowViewModel)
    {
        if (createAgentChatRequest.AgentDefinition is not { } agentDefinition)
        {
            // Manifest-based sessions are always local — TrustedExecutionRequest requires AgentDefinition.
            return await AgentFactory.CreateAgentChatAsync(createAgentChatRequest);
        }

        var localProfileEntityId = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var owningProfileEntityId = ReadOwningProfileEntityId(agentSessionEntityData);

        var targetClientInstance = (owningProfileEntityId != default && owningProfileEntityId != localProfileEntityId)
            ? owningProfileEntityId.ToString()
            : TrustProfile.LocalClientInstance;

        var trustProfile = TrustProfileComposer.Finalize(new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = [targetClientInstance],
        });

        var executor = this.trustedExecutorSelector.SelectExecutor(trustProfile, targetClientInstance);
        return await executor.CreateAgentChatAsync(new TrustedExecutionRequest
        {
            AgentDefinition = agentDefinition,
            TrustProfile = trustProfile,
            TargetClientInstance = targetClientInstance,
            AgentSessionId = createAgentChatRequest.AgentSessionId,
            AgentServices = createAgentChatRequest.AgentServices,
        });
    }

    private static EntityId ReadOwningProfileEntityId(JsonElement entityData)
    {
        if (entityData.TryGetProperty("owning-profile-entity-id", out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out var guid))
        {
            return new EntityId(guid);
        }

        return default;
    }

    public AgentViewModel BuildAgentViewModelPublic(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        string title,
        string agentSessionTabId)
    {
        return BuildAgentViewModel(mainWindowViewModel, loggerFactory, agentChat, title, agentSessionTabId);
    }

    private static AgentViewModel BuildAgentViewModel(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        string title,
        string agentSessionTabId)
    {
        return new AgentViewModel(agentChat, title, loggerFactory)
        {
            OpenUrlHandler = url => _ = mainWindowViewModel.OpenTabAsync(
                new WebViewModel(url, mainWindowViewModel)
                {
                    Id = $"web-{url}",
                    Title = url,
                },
                insertAfterTabId: agentSessionTabId,
                workspacePaneId: mainWindowViewModel.FindWorkspacePaneIdForTab(agentSessionTabId)),
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

    private static async Task UpdateAutoResumeInEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AutoResumeSettings? newSettings)
    {
        if (agentSessionEntity.Data is not JsonElement currentData)
        {
            return;
        }

        var node = JsonNode.Parse(currentData.GetRawText())!.AsObject();
        if (newSettings is null)
        {
            node.Remove("auto-resume");
        }
        else
        {
            var autoResumeNode = new JsonObject
            {
                ["trusted-executor"] = newSettings.TrustedExecutor,
            };
            if (newSettings.ResumePrompt is not null)
            {
                autoResumeNode["resume-prompt"] = newSettings.ResumePrompt;
            }

            node["auto-resume"] = autoResumeNode;
        }

        var updated = JsonSerializer.SerializeToElement(node);
        await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Update auto-resume for {agentSessionEntity.DisplayName}.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityChangeMode = EntityChangeMode.Replace,
                        ConcurrencyTag = agentSessionEntity.ConcurrencyTag,
                        Data = updated,
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
