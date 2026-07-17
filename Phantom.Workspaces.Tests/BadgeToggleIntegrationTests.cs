using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests badge toggling through SubscribedEntityViewModel to ensure update results are properly surfaced.
/// </summary>
public sealed class BadgeToggleIntegrationTests
{
    [AvaloniaFact]
    public async Task SubscribedEntityViewModel_ToggleInterestAsync_AppliesInterest()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);

        var taskId = new EntityId(Guid.NewGuid());
        await SeedTaskAsync(broker.EntityRepository.DataAccessLayer, taskId);

        // Get the task entity via broker (which wires up ToggleInterestAsync)
        var entities = await broker.GetEntitiesAsync(new[] { taskId }, ct);
        var subscribedEntity = entities.Single();

        // Toggle interest on
        await subscribedEntity.ToggleInterestAsync("actionable");

        // Verify the interest was applied
        var afterToggle = await GetEntityWithRelationshipsAsync(broker, taskId, ct);
        Assert.Contains(afterToggle.Relationships, relationship =>
            relationship.Data is { } data
            && data.TryGetProperty("entity-types", out var types)
            && types.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String && type.GetString() == "actionable"));
    }

    [AvaloniaFact]
    public async Task SubscribedEntityViewModel_ToggleInterestAsync_WhenUpdateFails_ThrowsException()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);

        // Create a bogus entity that doesn't exist (no ToggleInterestAsync wired)
        var bogusId = new EntityId(Guid.NewGuid());
        var bogusSnapshot = new EntitySnapshot
        {
            EntityId = bogusId,
            ConcurrencyTag = null,
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse("""{"entity-id":"00000000-0000-0000-0000-000000000000","entity-types":["entity", "task"]}""").RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        
        // Wire up toggle function manually for this test
        var toggleFunc = async (SubscribedEntityViewModel entity, string interestTypeName) =>
        {
            await InterestToggle.ToggleAsync(broker, entity.Snapshot, interestTypeName, ct);
        };
        var subscribedEntity = new SubscribedEntityViewModel(bogusSnapshot, null, toggleFunc);

        // Attempting to toggle interest on non-existent entity should throw
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await subscribedEntity.ToggleInterestAsync("actionable"));

        Assert.NotNull(exception);
    }

    [AvaloniaFact]
    public async Task SubscribedEntityViewModel_ToggleInterestAsync_RemovesExistingInterest()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);

        var taskId = new EntityId(Guid.NewGuid());
        await SeedTaskAsync(broker.EntityRepository.DataAccessLayer, taskId);

        // Get the task entity via broker
        var entities = await broker.GetEntitiesAsync(new[] { taskId }, ct);
        var subscribedEntity = entities.Single();

        // Toggle interest on
        await subscribedEntity.ToggleInterestAsync("actionable");

        // Refresh - get the updated entity WITH relationships so toggle knows about the existing interest
        var withInterestSnapshot = await GetEntityWithRelationshipsAsync(broker, taskId, ct);
        
        // Create a new SubscribedEntityViewModel with the relationship-aware snapshot
        var toggleFunc = async (SubscribedEntityViewModel entity, string interestTypeName) =>
        {
            await InterestToggle.ToggleAsync(broker, entity.Snapshot, interestTypeName, ct);
        };
        subscribedEntity = new SubscribedEntityViewModel(withInterestSnapshot, null, toggleFunc);

        // Toggle interest off
        await subscribedEntity.ToggleInterestAsync("actionable");

        // Verify the interest was removed
        var afterRemoval = await GetEntityWithRelationshipsAsync(broker, taskId, ct);
        Assert.DoesNotContain(afterRemoval.Relationships, relationship =>
            relationship.Data is { } data
            && data.TryGetProperty("entity-types", out var types)
            && types.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String && type.GetString() == "actionable"));
    }

    [AvaloniaFact]
    public async Task EntityListNodeViewModel_BadgeCommand_CanExecute_WhenEntityExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);

        var taskId = new EntityId(Guid.NewGuid());
        await SeedTaskAsync(broker.EntityRepository.DataAccessLayer, taskId);

        var entities = await broker.GetEntitiesAsync(new[] { taskId }, ct);
        var subscribedEntity = entities.Single();

        var cardNode = new EntityListNodeViewModel(
            subscribedEntity,
            new[] { "tasks", "test-task" },
            "test-task");

        var badgesModel = new BadgesModel();
        badgesModel.SetBadges(new[]
        {
            new BadgeModel("actionable", "📌", "Mark as actionable", IsActive: false),
        });

        var badgesViewModel = new BadgesViewModel(badgesModel);

        // Set up the badges
        cardNode.Card.SetBadges(badgesViewModel);

        Assert.NotNull(cardNode.Card.ToggleInterestCommand);
        Assert.True(cardNode.Card.ToggleInterestCommand.CanExecute(badgesViewModel.Badges.First()));
    }

    [AvaloniaFact]
    public void EntityListNodeViewModel_BadgeCommand_CannotExecute_WhenEntityIsNull()
    {
        // Create a card node without an entity (display-only node)
        var cardNode = new EntityListNodeViewModel(
            displayName: "Test Display Node",
            entityType: "task",
            nameComponents: new[] { "tasks", "display" },
            sortKey: "display");

        var badgesModel = new BadgesModel();
        badgesModel.SetBadges(new[]
        {
            new BadgeModel("actionable", "📌", "Mark as actionable", IsActive: false),
        });

        var badgesViewModel = new BadgesViewModel(badgesModel);

        // Set up the badges
        cardNode.Card.SetBadges(badgesViewModel);

        Assert.NotNull(cardNode.Card.ToggleInterestCommand);
        Assert.False(cardNode.Card.ToggleInterestCommand.CanExecute(badgesViewModel.Badges.First()));
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_ToggleInterestCommand_WhenToggleFails_PropagatesException()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);

        // Create a bogus entity that doesn't exist
        var bogusId = new EntityId(Guid.NewGuid());
        var bogusSnapshot = new EntitySnapshot
        {
            EntityId = bogusId,
            ConcurrencyTag = null,
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse("""{"entity-id":"00000000-0000-0000-0000-000000000000","entity-types":["entity", "task"]}""").RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        
        // Wire up toggle function that will fail
        var toggleFunc = async (SubscribedEntityViewModel entity, string interestTypeName) =>
        {
            await InterestToggle.ToggleAsync(broker, entity.Snapshot, interestTypeName, ct);
        };
        var subscribedEntity = new SubscribedEntityViewModel(bogusSnapshot, null, toggleFunc);

        var cardNode = new EntityListNodeViewModel(
            subscribedEntity,
            new[] { "tasks", "bogus-task" },
            "bogus-task");

        var badgesModel = new BadgesModel();
        badgesModel.SetBadges(new[]
        {
            new BadgeModel("actionable", "📌", "Mark as actionable", IsActive: false),
        });

        var badgesViewModel = new BadgesViewModel(badgesModel);

        // Set up the badges (this wires up ToggleInterestCommand)
        cardNode.Card.SetBadges(badgesViewModel);

        // Execute the command - this should propagate the exception via the LastExecutionTask
        Assert.NotNull(cardNode.Card.ToggleInterestCommand);
        cardNode.Card.ToggleInterestCommand.Execute(badgesViewModel.Badges.First());

        // Get the underlying task from AsyncRelayCommand
        var asyncCommand = Assert.IsType<AsyncRelayCommand>(cardNode.Card.ToggleInterestCommand);
        var executionTask = asyncCommand.LastExecutionTask;
        Assert.NotNull(executionTask);

        // The task should fail with an exception
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await executionTask);
        Assert.NotNull(exception);
    }

    private static async Task SeedTaskAsync(IDataAccessLayer dataAccessLayer, EntityId id)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{id.Value}}",
              "entity-types": ["entity", "task"],
              "names": [["tasks", "test-task"]],
              "display-name": { "default": "Test Task" }
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed task" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = id,
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, System.Threading.CancellationToken.None);

        var failure = result.EntityResults.FirstOrDefault(static entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.True(failure is null, failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static error => error.Message)));
    }

    private static async Task<EntitySnapshot> GetEntityWithRelationshipsAsync(EntityBroker broker, EntityId entityId, System.Threading.CancellationToken ct)
    {
        var result = await broker.EntityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                        RelationshipsToReturn = [new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet([]) }],
                    },
                ],
            },
            ct);
        return result.Batches.SelectMany(batch => batch.Entities).Single(entity => entity.EntityId == entityId);
    }
}
