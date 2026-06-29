using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
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
    private AgentDefinitionItem? selectedAgentDefinition;
    private bool isCreatingSession;

    public StartAgentSessionOnProfileViewModel(
        IWorkspaceTabService tabService,
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel profileEntity)
    {
        this.tabService = tabService;
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
        this.mainWindowViewModel = mainWindowViewModel;
        this.profileEntity = profileEntity;
        this.CreateSessionCommand = new RelayCommand(async _ => await this.CreateSessionAsync(), _ => this.CanCreateSession());
        _ = this.LoadAgentDefinitionsAsync();
    }

    public ObservableCollection<AgentDefinitionItem> AgentDefinitions { get; } = new();

    public AgentDefinitionItem? SelectedAgentDefinition
    {
        get => this.selectedAgentDefinition;
        set
        {
            if (this.SetProperty(ref this.selectedAgentDefinition, value))
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
        return this.SelectedAgentDefinition is not null && !this.IsCreatingSession;
    }

    private async Task LoadAgentDefinitionsAsync()
    {
        try
        {
            var queryRequest = new QueryRequest
            {
                Clauses =
                [
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

            var queryResult = await this.mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer.QueryAsync(queryRequest);
            var agentDefinitionSnapshots = queryResult.Batches
                .SelectMany(batch => batch.Entities)
                .ToList();

            var agentDefinitionEntities = await this.mainWindowViewModel.EntityBroker.GetEntitiesAsync(
                agentDefinitionSnapshots.Select(snapshot => snapshot.EntityId).ToArray());

            foreach (var entity in agentDefinitionEntities)
            {
                if (entity.Data is JsonElement entityData
                    && entityData.TryGetProperty("definition", out var definitionElement))
                {
                    var agentDefinition = AgentDefinition.FromJson(definitionElement.GetRawText());
                    var displayName = entity.DisplayName ?? "Unknown Agent";
                    this.AgentDefinitions.Add(new AgentDefinitionItem
                    {
                        Entity = entity,
                        Definition = agentDefinition,
                        DisplayName = displayName,
                    });
                }
            }
        }
        catch (Exception)
        {
            // TODO: Show error message
        }
    }

    private async Task CreateSessionAsync()
    {
        if (this.SelectedAgentDefinition is null)
        {
            return;
        }

        this.IsCreatingSession = true;
        try
        {
            var agentDefinition = this.SelectedAgentDefinition.Definition;
            var agentServices = await this.agentSessionShortcutContext.CreateAgentServicesAsync(this.mainWindowViewModel);
            
            // Create agent chat
            var agentChat = await AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest
                {
                    AgentDefinition = agentDefinition,
                    AgentServices = agentServices,
                });

            // Create agent session entity using the existing helper
            var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
                this.mainWindowViewModel,
                this.SelectedAgentDefinition.Entity,
                agentChat.AgentSessionId,
                owningProfileEntityId: this.profileEntity.EntityId);
            
            if (createdAgentSessionEntity is null)
            {
                // Dispose the agent chat if entity creation failed
                await agentChat.DisposeAsync();
                return;
            }

            // Create the AgentSessionWorkspaceTabViewModel
            var agentSessionTab = await this.openAgentSessionShortcutHandler.CreateAgentSessionTabAsync(
                this.mainWindowViewModel,
                createdAgentSessionEntity,
                agentChat);

            // Replace this tab with the agent session tab
            await this.tabService.ReplaceTabAsync(this, agentSessionTab);
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

    public sealed class AgentDefinitionItem
    {
        public required SubscribedEntityViewModel Entity { get; init; }
        public required AgentDefinition Definition { get; init; }
        public required string DisplayName { get; init; }
    }
}
