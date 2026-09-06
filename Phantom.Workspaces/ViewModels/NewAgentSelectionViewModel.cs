using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Workspace "New Agent" selection tab (issue #1461). Presents a manifest picker and hosts the
/// shared <see cref="ManifestParametersViewModel"/> (issue #1463) for parameter editing. Selecting a
/// manifest drives <see cref="ManifestParametersViewModel.SetManifest"/> (which refreshes rows and
/// persists entered values by name). A <c>default</c> relationship on the workspace (or its profile)
/// pre-selects that manifest. On confirm it launches the selected manifest as an agent session with
/// the entered parameters, dismisses this selection tab, creates a <c>related</c> relationship
/// between the workspace and the new agent session, and saves the workspace.
/// </summary>
public sealed class NewAgentSelectionViewModel : WorkspaceTabViewModel
{
    private const string RelatedNote = "Workspace save records the live entity tabs associated with this workspace.";

    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly WorkspacePaneViewModel workspacePane;
    private readonly TaskCompletionSource loadedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AgentSourceItem? selectedManifest;
    private bool isConfirming;

    public NewAgentSelectionViewModel(
        MainWindowViewModel mainWindowViewModel,
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        WorkspacePaneViewModel workspacePane)
    {
        this.mainWindowViewModel = mainWindowViewModel;
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
        this.workspacePane = workspacePane;

        this.ManifestParameters = new ManifestParametersViewModel(mainWindowViewModel);
        this.ManifestParameters.PropertyChanged += this.OnManifestParametersPropertyChanged;

        this.ConfirmCommand = new RelayCommand(
            async _ => await this.ConfirmAsync(),
            _ => this.CanConfirm());

        Lifetime.Run(this.LoadManifestsAsync);
    }

    /// <summary>The hosted shared manifest-parameter component (issue #1463).</summary>
    public ManifestParametersViewModel ManifestParameters { get; }

    /// <summary>The selectable manifest/definition entities discovered for this workspace.</summary>
    public ObservableCollection<AgentSourceItem> Manifests { get; } = new();

    /// <summary>Completes once the manifest list has loaded and default pre-selection ran. For tests.</summary>
    public Task Loaded => this.loadedTcs.Task;

    public AgentSourceItem? SelectedManifest
    {
        get => this.selectedManifest;
        set
        {
            if (this.SetProperty(ref this.selectedManifest, value))
            {
                if (value is not null)
                {
                    this.ManifestParameters.SetManifest(value.Entity);
                }

                this.ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConfirming
    {
        get => this.isConfirming;
        private set
        {
            if (this.SetProperty(ref this.isConfirming, value))
            {
                this.ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand ConfirmCommand { get; }

    private bool CanConfirm()
        => this.SelectedManifest is not null
            && !this.IsConfirming
            && this.ManifestParameters.IsValid;

    private void OnManifestParametersPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManifestParametersViewModel.IsValid))
        {
            this.ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task LoadManifestsAsync(CancellationToken ct = default)
    {
        try
        {
            var dataAccessLayer = this.mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;

            // Same manifest-picking query as StartAgentSessionOnProfileViewModel.LoadAgentSourcesAsync.
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

            var queryResult = await dataAccessLayer.QueryAsync(queryRequest, ct);
            var snapshotIds = queryResult.Batches
                .SelectMany(batch => batch.Entities)
                .Select(snapshot => snapshot.EntityId)
                .Distinct()
                .ToArray();

            var entities = await this.mainWindowViewModel.EntityBroker.GetEntitiesAsync(snapshotIds);

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

                this.Manifests.Add(new AgentSourceItem
                {
                    Entity = entity,
                    DisplayName = entity.DisplayName ?? "Unknown Agent",
                });
            }

            var defaultEntityId = await this.ResolveDefaultManifestEntityIdAsync(dataAccessLayer);
            if (defaultEntityId is { } id)
            {
                var defaultItem = this.Manifests.FirstOrDefault(item => item.Entity.EntityId == id);
                if (defaultItem is not null)
                {
                    this.SelectedManifest = defaultItem;
                }
            }
        }
        catch (Exception)
        {
            // Best-effort load; leave the picker empty on failure.
        }
        finally
        {
            this.loadedTcs.TrySetResult();
        }
    }

    private async Task<EntityId?> ResolveDefaultManifestEntityIdAsync(IDataAccessLayer dataAccessLayer)
    {
        // A default manifest can be scoped directly to the workspace, or to its owning profile.
        var byWorkspace = await DefaultRelationshipResolver.FindDefaultValueAppliedToAsync(
            dataAccessLayer, this.workspacePane.Entity.EntityId);
        if (byWorkspace is not null)
        {
            return byWorkspace;
        }

        var profileEntityId = this.mainWindowViewModel.EntityBroker.EntityRepository
            .WorkspaceEntitySession.UserComputerProfileEntityId;
        if (profileEntityId != default)
        {
            return await DefaultRelationshipResolver.FindDefaultValueAppliedToAsync(
                dataAccessLayer, profileEntityId);
        }

        return null;
    }

    /// <summary>Internal so tests can await the full confirm flow (launch → dismiss → relate →
    /// save) deterministically, which the fire-and-forget <see cref="ConfirmCommand"/> cannot expose.</summary>
    internal async Task ConfirmAsync()
    {
        if (this.SelectedManifest is null)
        {
            return;
        }

        this.IsConfirming = true;
        try
        {
            var parameterValues = this.ManifestParameters.GetValues();
            var parameterSelections = this.ManifestParameters.GetSelections();
            this.ManifestParameters.CommitLaunch();

            // (a) Launch the selected manifest as an agent session in the workspace pane.
            // Host the launch's background chat wiring on the window lifetime (not this tab's
            // lifetime): this tab is dismissed in step (b) immediately after the launch begins,
            // which disposes its lifetime — so the wiring must survive on a durable lifetime.
            var createdAgentSessionEntity = await AgentManifestSessionLauncher.LaunchAsync(
                this.mainWindowViewModel.BackgroundLifetime,
                this.mainWindowViewModel,
                this.agentSessionShortcutContext,
                this.openAgentSessionShortcutHandler,
                this.SelectedManifest.Entity,
                parameterValues.Count > 0 ? parameterValues : null,
                parameterSelections.Count > 0 ? parameterSelections : null,
                workspacePaneId: this.workspacePane.Id);

            if (createdAgentSessionEntity is null)
            {
                return;
            }

            // (b) Dismiss the manifest-selection tab so it is not present alongside the agent.
            this.mainWindowViewModel.CloseTab(this);

            // (c) Relate the workspace to the new agent session.
            await this.AddRelatedRelationshipAsync(
                this.workspacePane.Entity.EntityId,
                createdAgentSessionEntity.EntityId);

            // (d) Save the workspace through the existing save path.
            this.workspacePane.SaveCommand.Execute(null);
            if (this.workspacePane.SaveCommand.LastExecutionTask is { } saveTask)
            {
                await saveTask;
            }
        }
        finally
        {
            this.IsConfirming = false;
        }
    }

    private async Task AddRelatedRelationshipAsync(EntityId workspaceId, EntityId agentSessionId)
    {
        var relationshipId = Guid.NewGuid();
        var relationshipData = new JsonObject
        {
            ["entity-id"] = relationshipId.ToString(),
            ["entity-types"] = new JsonArray("entity", "relationship", "related"),
            ["participants"] = new JsonObject
            {
                ["entities"] = new JsonArray(workspaceId.Value.ToString(), agentSessionId.Value.ToString()),
            },
            ["note"] = RelatedNote,
        };

        using var doc = JsonDocument.Parse(relationshipData.ToJsonString());

        await this.mainWindowViewModel.EntityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "Relate new agent session to workspace." },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(relationshipId),
                    Data = doc.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
    }

    public sealed class AgentSourceItem
    {
        public required SubscribedEntityViewModel Entity { get; init; }

        public required string DisplayName { get; init; }

        public bool IsManifest => this.Entity.IsEntityType("agent-manifest");
    }
}
