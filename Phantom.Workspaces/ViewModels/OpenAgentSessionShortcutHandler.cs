using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Utilities;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenAgentSessionShortcutHandler : ShortcutHandler, IAsyncDisposable
{
    private readonly ViewModelLifetime lifetime = new();
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly ITrustedExecutorSelector trustedExecutorSelector;
    private readonly IRunningAgentChatTable runningAgentChatTable;

    /// <summary>
    /// The running-agent-chat table used by this handler. Exposed so co-located view models that
    /// share the launchpad → session flow (see <c>AgentManifestLaunchpadViewModel</c>) can acquire
    /// chats through the same factory-mediated path required by #1109 / #1180 rather than the
    /// static <see cref="AgentFactory.CreateAgentChatAsync"/> helper, which bypasses
    /// <see cref="AgentChatFactory"/>'s <c>WithSelfAsFactory</c> self-injection.
    /// </summary>
    internal IRunningAgentChatTable RunningAgentChatTable => this.runningAgentChatTable;

    public OpenAgentSessionShortcutHandler(
        AgentSessionShortcutContext agentSessionShortcutContext,
        ITrustedExecutorSelector trustedExecutorSelector,
        IRunningAgentChatTable runningAgentChatTable)
    {
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.trustedExecutorSelector = trustedExecutorSelector;
        this.runningAgentChatTable = runningAgentChatTable ?? throw new ArgumentNullException(nameof(runningAgentChatTable));
    }

    public ValueTask DisposeAsync() => lifetime.DisposeAsync();

    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Open
            && entityViewModel.IsEntityType("agent-session"));
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
            WorkspacePaneId = paneId,
        };
        await mainWindowViewModel.OpenTabAsync(loadingTab);

        // Complete initialization in the background
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        lifetime.Run(ct => InitializeTabInBackgroundAsync(mainWindowViewModel, entityViewModel, loadingTab, foregroundScheduler));

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
            var result = await this.TryBuildAgentAsync(mainWindowViewModel, agentSessionEntity, tab, foregroundScheduler);
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
        string? agentSessionId = null;
        if (agentSessionEntity.Data is System.Text.Json.JsonElement entityDataElement
            && entityDataElement.TryGetProperty("agent-session-id", out var agentSidEl)
            && agentSidEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            agentSessionId = agentSidEl.GetString();
        }

        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = tabId ?? agentSessionEntity.EntityId.ToString(),
            Title = !string.IsNullOrEmpty(title)
                ? title
                : !string.IsNullOrEmpty(agentSessionEntity.DisplayName)
                    ? agentSessionEntity.DisplayName
                    : agentSessionEntity.EntityId.ToString(),
            DockRegion = dockRegion ?? "full",
            Entity = agentSessionEntity,
            NotificationService = mainWindowViewModel.NotificationService,
            AgentSessionId = agentSessionId,
            WorkspacePaneId = mainWindowViewModel.SelectedWorkspacePane?.Id,
        };

        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        lifetime.Run(ct => InitializeTabInBackgroundAsync(mainWindowViewModel, agentSessionEntity, loadingTab, foregroundScheduler));

        return loadingTab;
    }

    /// <summary>
    /// #1129: Restore-aware factory override that routes the workspace-open/restore path
    /// through the shortcut pipeline. Delegates to
    /// <see cref="TryCreateAgentSessionTabForRestoreAsync"/> so agent-session entities keep
    /// producing an <see cref="AgentSessionWorkspaceTabViewModel"/> (not the generic entity
    /// card) while preserving the saved tab metadata.
    /// </summary>
    public override async Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel,
        string? tabId,
        string? title,
        string? dockRegion)
    {
        return await this.TryCreateAgentSessionTabForRestoreAsync(
            mainWindowViewModel, entityViewModel, tabId, title, dockRegion);
    }

    /// <summary>
    /// Creates an agent chat for the given session in the <see cref="IRunningAgentChatTable"/>,
    /// enqueues <paramref name="resumePrompt"/> as the first user message, and returns the
    /// acquired lease. Returns <see langword="null"/> when the entity data is missing required fields.
    /// </summary>
    internal async Task<RunningAgentChatLease?> TryStartAutoResumeAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        string resumePrompt,
        TaskScheduler foregroundScheduler)
    {
        if (agentSessionEntity.Data is not JsonElement agentSessionEntityData
            || !agentSessionEntityData.TryGetProperty("agent-session-id", out var agentSessionIdElement)
            || agentSessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentSessionIdElement.GetString()))
        {
            return null;
        }

        var agentSessionId = agentSessionIdElement.GetString();
        var parameterValues = agentSessionEntityData.TryGetProperty("parameter-values", out var pvElement)
            ? ReadStringDictionary(pvElement)
            : null;

        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel);

        var lease = await this.runningAgentChatTable.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId(agentSessionId!),
                AgentSessionEntity = agentSessionEntityData,
                AgentServices = agentServices,
                ForegroundScheduler = foregroundScheduler,
                ToolResourceFactory = agentServices.ToolResourceFactory,
                Parameters = parameterValues,
                AgentDefinitionResolver = CreateAgentDefinitionResolver(mainWindowViewModel),
                EntityName = agentSessionEntity.DisplayName,
                EntityId = agentSessionEntity.EntityId.ToString(),
                // #1135: For auto-resume, the session's owning workspace is the currently-selected
                // pane at auto-resume time (the pane the tab will be restored into).
                WorkspaceId = mainWindowViewModel.SelectedWorkspacePane?.Id,
            });

        lease.AgentChat.EnqueueUserMessage(resumePrompt);
        return lease;
    }

    public async Task<AgentSessionWorkspaceTabViewModel> CreateAgentSessionTabAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentChat agentChat)
    {
        // #1122: Capture the UI-thread scheduler synchronously before any awaits so it truly
        // reflects the calling thread's SynchronizationContext, then thread it through to
        // AgentViewModel so its sub-agent restore continuation mutates UI-bound state on the
        // UI thread.
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var loggerFactory = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = agentSessionEntity.EntityId.ToString(),
            Title = agentSessionEntity.DisplayName,
            DockRegion = "full",
            Entity = agentSessionEntity,
            NotificationService = mainWindowViewModel.NotificationService,
            AgentSessionId = agentChat.AgentSessionId,
            WorkspacePaneId = mainWindowViewModel.SelectedWorkspacePane?.Id,
        };
        // #1429: materialize through the single seam so slash commands are wired on this path too.
        var agent = this.ComposeSessionAgentViewModel(
            mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity, tab, foregroundScheduler);
        tab.SetReady(agent, loggerFactory);
        return tab;
    }

    private async Task<(AgentViewModel agent, ObservableLoggerFactory loggerFactory, RunningAgentChatLease? lease)?> TryBuildAgentAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity,
        AgentSessionWorkspaceTabViewModel tab,
        TaskScheduler foregroundScheduler)
    {
        if (agentSessionEntity.Data is not JsonElement agentSessionEntityData
            || !agentSessionEntityData.TryGetProperty("agent-session-id", out var agentSessionIdElement)
            || agentSessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentSessionIdElement.GetString()))
        {
            return null;
        }

        var agentSessionId = agentSessionIdElement.GetString();
        var parameterValues = agentSessionEntityData.TryGetProperty("parameter-values", out var pvElement)
            ? ReadStringDictionary(pvElement)
            : null;

        var loggerFactory = new ObservableLoggerFactory();
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(mainWindowViewModel, loggerFactory);

        AgentChat agentChat;
        RunningAgentChatLease? lease = null;

        // Extract display-name and description from entity data to populate AgentChat properties
        string? entityDisplayName = null;
        string? entityDescription = null;
        if (agentSessionEntityData.TryGetProperty("display-name", out var displayNameElement)
            && displayNameElement.TryGetProperty("default", out var displayNameDefaultElement)
            && displayNameDefaultElement.ValueKind == JsonValueKind.String)
        {
            entityDisplayName = displayNameDefaultElement.GetString();
        }
        if (agentSessionEntityData.TryGetProperty("description", out var descriptionElement)
            && descriptionElement.ValueKind == JsonValueKind.String)
        {
            entityDescription = descriptionElement.GetString();
        }

        var localProfileEntityId = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var hostProfileEntityId = ReadHostProfileEntityId(agentSessionEntityData);
        var targetClientInstance = hostProfileEntityId != default
            && hostProfileEntityId != localProfileEntityId
            ? hostProfileEntityId.ToString()
            : TrustProfile.LocalClientInstance;
        var agentDefinitionResolver = CreateAgentDefinitionResolver(mainWindowViewModel);

        if (!string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal))
        {
            var resolvedDefinition = await agentDefinitionResolver.ResolveAsync(
                new AgentDefinitionResolveRequest
                {
                    AgentSessionEntity = agentSessionEntityData,
                    ToolResourceFactory = agentServices.ToolResourceFactory,
                    Parameters = parameterValues,
                });
            if (resolvedDefinition is null)
            {
                return null;
            }

            agentChat = await this.CreateTrustedAgentChatAsync(
                resolvedDefinition.Definition,
                agentSessionId!,
                agentServices,
                targetClientInstance);
        }
        else
        {
            lease = await this.runningAgentChatTable.AcquireAsync(
                new AcquireAgentChatRequest
                {
                    AgentSessionId = new AgentSessionId(agentSessionId!),
                    AgentSessionEntity = agentSessionEntityData,
                    AgentServices = agentServices,
                    ForegroundScheduler = foregroundScheduler,
                    ToolResourceFactory = agentServices.ToolResourceFactory,
                    Parameters = parameterValues,
                    AgentDefinitionResolver = agentDefinitionResolver,
                    EntityName = agentSessionEntity.DisplayName,
                    EntityId = agentSessionEntity.EntityId.ToString(),
                    EntityDisplayName = entityDisplayName,
                    EntityDescription = entityDescription,
                    // #1135: Stamp the pane the session was started/opened in so cross-workspace
                    // status-button clicks (running-agent brain) can switch to it before focusing.
                    WorkspaceId = tab.WorkspacePaneId,
                });
            agentChat = lease.AgentChat;
        }

        // #1429: build + wire slash commands through the single GUI session-composition seam so this
        // path can never diverge from the other launch paths.
        var agent = this.ComposeSessionAgentViewModel(
            mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity, tab, foregroundScheduler);

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

    private async Task<AgentChat> CreateTrustedAgentChatAsync(
        AgentDefinition agentDefinition,
        string agentSessionId,
        AgentServices agentServices,
        string targetClientInstance)
    {
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
            AgentSessionId = agentSessionId,
            AgentServices = agentServices,
        });
    }

    private static EntityId ReadHostProfileEntityId(JsonElement entityData)
    {
        // Prefer the schema-canonical field; fall back to the legacy alias so sessions persisted
        // before the field name was unified still route to the correct hosting profile.
        foreach (var propertyName in new[] { "host-profile-entity-id", "owning-profile-entity-id" })
        {
            if (entityData.TryGetProperty(propertyName, out var element)
                && element.ValueKind == JsonValueKind.String
                && Guid.TryParse(element.GetString(), out var guid))
            {
                return new EntityId(guid);
            }
        }

        return default;
    }

    private static AgentDefinitionResolver CreateAgentDefinitionResolver(MainWindowViewModel mainWindowViewModel)
        => new(mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer);

    /// <summary>
    /// Single GUI session-composition seam (#1429). Builds the session <see cref="AgentViewModel"/> and
    /// ALWAYS wires slash-command handling via <see cref="AgentViewModel.ConfigureSlashCommands"/>. Every
    /// launch path — agent-session, agent-definition, agent-manifest, and profile→definition — materializes
    /// its session view model through here, so no path can produce a session with inert slash commands. The
    /// per-launch inputs (session entity, tab, trusted-executor identity, rename/title/clone callbacks) are
    /// derived from the required parameters, so a new launch path cannot bypass the wiring.
    /// </summary>
    public AgentViewModel ComposeSessionAgentViewModel(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        SubscribedEntityViewModel agentSessionEntity,
        AgentSessionWorkspaceTabViewModel tab,
        TaskScheduler foregroundScheduler)
    {
        var agent = BuildAgentViewModel(
            mainWindowViewModel, loggerFactory, agentChat, agentSessionEntity.DisplayName, tab.Id, foregroundScheduler);

        var trustedExecutorIdentifier = ResolveTrustedExecutorIdentifier(mainWindowViewModel, agentSessionEntity);

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
                RenameSessionAsync = async (newName, ct) =>
                {
                    await agentSessionEntity.SaveDisplayNameAsync(newName);
                    tab.SetTitleExplicit(newName);
                },
                SetTabTitleAsync = (newTitle, ct) =>
                {
                    tab.SetTitleExplicit(newTitle);
                    return Task.CompletedTask;
                },
                ReplaceWithCloneAsync = async ct =>
                {
                    var cloneTab = await this.CreateCloneTabAsync(mainWindowViewModel, agentSessionEntity, tab, ct).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(async () => await mainWindowViewModel.ReplaceTabAsync(tab, cloneTab));
                },
                OpenCloneInNewTabAsync = async ct =>
                {
                    var cloneTab = await this.CreateCloneTabAsync(mainWindowViewModel, agentSessionEntity, tab, ct).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(async () => await mainWindowViewModel.OpenTabAsync(cloneTab));
                },
            });

        return agent;
    }

    private static string ResolveTrustedExecutorIdentifier(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel agentSessionEntity)
    {
        var localProfileEntityId = mainWindowViewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var hostProfileEntityId = agentSessionEntity.Data is JsonElement data ? ReadHostProfileEntityId(data) : default;
        return hostProfileEntityId != default && hostProfileEntityId != localProfileEntityId
            ? hostProfileEntityId.ToString()
            : TrustProfile.LocalClientInstance;
    }

    private static AgentViewModel BuildAgentViewModel(
        MainWindowViewModel mainWindowViewModel,
        ObservableLoggerFactory loggerFactory,
        AgentChat agentChat,
        string title,
        string agentSessionTabId,
        TaskScheduler foregroundScheduler)
    {
        // #1122: foregroundScheduler is a required constructor parameter on AgentViewModel so
        // sub-agent restore continuations run on the UI thread. Callers capture the scheduler
        // on the UI thread and thread it through.
        return new AgentViewModel(agentChat, title, agentChat.Description, loggerFactory, foregroundScheduler)
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

    private async Task<AgentSessionWorkspaceTabViewModel> CreateCloneTabAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel sourceEntity,
        AgentSessionWorkspaceTabViewModel currentTab,
        CancellationToken cancellationToken)
    {
        if (sourceEntity.Data is not JsonElement sourceData)
        {
            throw new InvalidOperationException("Cannot clone an agent session without entity data.");
        }

        var cloneEntityId = new EntityId();
        var cloneName = DisplayNameSuffixHelper.GetNextAvailableName(sourceEntity.DisplayName, [sourceEntity.DisplayName]);
        var cloneData = EntityCloneHelper.RewriteEntityId(sourceData, cloneEntityId);
        var cloneNode = JsonNode.Parse(cloneData.GetRawText())!.AsObject();
        cloneNode["agent-session-id"] = Guid.NewGuid().ToString("D");
        if (cloneNode["display-name"] is not JsonObject displayName)
        {
            displayName = [];
            cloneNode["display-name"] = displayName;
        }
        displayName["default"] = cloneName;

        using var document = JsonDocument.Parse(cloneNode.ToJsonString());
        await mainWindowViewModel.EntityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = $"Clone agent session {sourceEntity.DisplayName}." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = cloneEntityId,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });

        cancellationToken.ThrowIfCancellationRequested();

        var cloneEntity = (await mainWindowViewModel.EntityBroker.GetEntitiesAsync([cloneEntityId]))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Cloned agent session could not be loaded.");
        return await this.TryCreateAgentSessionTabForRestoreAsync(
                mainWindowViewModel,
                cloneEntity,
                tabId: $"{mainWindowViewModel.SelectedWorkspacePane?.Id}-{cloneEntityId}",
                title: cloneName,
                dockRegion: currentTab.DockRegion)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cloned agent session tab could not be created.");
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


