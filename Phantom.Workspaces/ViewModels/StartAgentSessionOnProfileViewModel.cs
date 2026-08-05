using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.ViewModels;

public sealed class StartAgentSessionOnProfileViewModel : WorkspaceTabViewModel
{
    private readonly IWorkspaceTabService tabService;
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly SubscribedEntityViewModel profileEntity;
    private readonly EntityId? preSelectedEntityId;
    private readonly IReadOnlyDictionary<string, string>? initialParameterValues;
    private AgentSourceItem? selectedAgentSource;
    private bool isCreatingSession;

    public StartAgentSessionOnProfileViewModel(
        IWorkspaceTabService tabService,
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel profileEntity,
        EntityId? preSelectedEntityId = null,
        IReadOnlyDictionary<string, string>? initialParameterValues = null)
    {
        this.tabService = tabService;
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
        this.mainWindowViewModel = mainWindowViewModel;
        this.profileEntity = profileEntity;
        this.preSelectedEntityId = preSelectedEntityId;
        this.initialParameterValues = initialParameterValues;
        this.CreateSessionCommand = new RelayCommand(async _ => await this.CreateSessionAsync(), _ => this.CanCreateSession());
        Lifetime.Run(this.LoadAgentSourcesAsync);
    }

    public IReadOnlyDictionary<string, string>? InitialParameterValues => this.initialParameterValues;

    public ObservableCollection<AgentSourceItem> AgentSources { get; } = new();

    public AgentSourceItem? SelectedAgentSource
    {
        get => this.selectedAgentSource;
        set
        {
            if (this.SetProperty(ref this.selectedAgentSource, value))
            {
                this.CreateSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCreatingSession
    {
        get => this.isCreatingSession;
        set
        {
            if (this.SetProperty(ref this.isCreatingSession, value))
            {
                this.CreateSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand CreateSessionCommand { get; }

    private bool CanCreateSession()
    {
        return this.SelectedAgentSource is not null && !this.IsCreatingSession;
    }

    private async Task LoadAgentSourcesAsync(CancellationToken ct = default)
    {
        try
        {
            var dataAccessLayer = this.mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;

            var queryRequest = new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "agent-manifests" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = ["agent-manifest"] },
                        },
                    },
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "agent-definitions" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = ["agent-definition"] },
                        },
                    },
                ],
            };

            var queryResult = await dataAccessLayer.QueryAsync(queryRequest);
            var snapshotIds = queryResult.Batches
                .SelectMany(batch => batch.Entities)
                .Select(snapshot => snapshot.EntityId)
                .Distinct()
                .ToArray();

            var entities = await this.mainWindowViewModel.EntityBroker.GetEntitiesAsync(snapshotIds);

            AgentSourceItem? preSelected = null;

            foreach (var entity in entities)
            {
                if (entity.Data is not JsonElement entityData)
                {
                    continue;
                }

                if (!entityData.TryGetProperty("manifest", out _) && !entityData.TryGetProperty("definition", out _))
                {
                    continue;
                }

                var item = new AgentSourceItem
                {
                    Entity = entity,
                    DisplayName = entity.DisplayName ?? "Unknown Agent",
                };

                this.AgentSources.Add(item);

                if (this.preSelectedEntityId is { } id && entity.EntityId == id)
                {
                    preSelected = item;
                }
            }

            if (preSelected is not null)
            {
                this.SelectedAgentSource = preSelected;
            }
        }
        catch (Exception)
        {
            // TODO: Show error message
        }
    }

    private async Task CreateSessionAsync()
    {
        if (this.SelectedAgentSource is null)
        {
            return;
        }

        this.IsCreatingSession = true;
        try
        {
            if (this.SelectedAgentSource.IsManifest)
            {
                await this.OpenManifestLaunchpadAsync(this.SelectedAgentSource.Entity);
            }
            else
            {
                await this.CreateDefinitionSessionAsync(this.SelectedAgentSource.Entity);
            }
        }
        catch (Exception)
        {
            // TODO: Show error message
        }
        finally
        {
            this.IsCreatingSession = false;
        }
    }

    private async Task OpenManifestLaunchpadAsync(SubscribedEntityViewModel manifestEntity)
    {
        var launchpadTab = new AgentManifestLaunchpadViewModel(
            manifestEntity,
            this.agentSessionShortcutContext,
            this.openAgentSessionShortcutHandler,
            this.mainWindowViewModel,
            this.initialParameterValues)
        {
            Id = $"launchpad-{manifestEntity.EntityId}",
            Title = manifestEntity.DisplayName,
            DockRegion = this.DockRegion,
            Entity = manifestEntity,
            TabHeader = TabHeaderViewModel.WithIcon("rocket", manifestEntity.DisplayName),
        };

        await this.tabService.ReplaceTabAsync(this, launchpadTab);
    }

    private async Task CreateDefinitionSessionAsync(SubscribedEntityViewModel definitionEntity)
    {
        if (definitionEntity.Data is not JsonElement entityData
            || !entityData.TryGetProperty("definition", out var definitionElement))
        {
            return;
        }

        var agentDefinition = AgentDefinition.FromJson(definitionElement.GetRawText());
        var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(this.mainWindowViewModel);

        var agentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentServices = agentServices,
            });

        var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            this.mainWindowViewModel,
            definitionEntity,
            agentChat.AgentSessionId,
            hostProfileEntityId: this.profileEntity.EntityId);

        if (createdAgentSessionEntity is null)
        {
            await agentChat.DisposeAsync();
            return;
        }

        var agentSessionTab = await this.openAgentSessionShortcutHandler.CreateAgentSessionTabAsync(
            this.mainWindowViewModel,
            createdAgentSessionEntity,
            agentChat);

        await this.tabService.ReplaceTabAsync(this, agentSessionTab);
    }

    public sealed class AgentSourceItem
    {
        public required SubscribedEntityViewModel Entity { get; init; }
        public required string DisplayName { get; init; }

        public bool IsManifest => this.Entity.IsEntityType("agent-manifest");
    }
}
