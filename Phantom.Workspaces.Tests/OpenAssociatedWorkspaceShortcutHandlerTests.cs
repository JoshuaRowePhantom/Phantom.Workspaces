using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class OpenAssociatedWorkspaceShortcutHandlerTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithRelatedWorkspace_ReturnsTrue()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var workspaceId = new EntityId(Guid.NewGuid());
        var workspace = await SeedEntityAsync(viewModel, workspaceId, "workspace");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship("related", new EntityId(Guid.NewGuid()), sourceId: null, workspaceId));

        var applies = await new OpenAssociatedWorkspaceShortcutHandler()
            .ShouldApplyTo(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(workspace);
        Assert.True(applies);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithNoRelationships_ReturnsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var source = CreateEntity(new EntityId(Guid.NewGuid()), "task");

        var applies = await new OpenAssociatedWorkspaceShortcutHandler()
            .ShouldApplyTo(viewModel, Shortcut.OpenWorkspace, source);

        Assert.False(applies);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithRelatedNonWorkspace_ReturnsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var participantId = new EntityId(Guid.NewGuid());
        var participant = await SeedEntityAsync(viewModel, participantId, "task");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship("related", new EntityId(Guid.NewGuid()), sourceId: null, participantId));

        var applies = await new OpenAssociatedWorkspaceShortcutHandler()
            .ShouldApplyTo(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(participant);
        Assert.False(applies);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_WorkspaceEntity_ReturnsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var relatedWorkspaceId = new EntityId(Guid.NewGuid());
        var relatedWorkspace = await SeedEntityAsync(viewModel, relatedWorkspaceId, "workspace");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "workspace",
            CreateRelationship("related", new EntityId(Guid.NewGuid()), sourceId: null, relatedWorkspaceId));

        var applies = await new OpenAssociatedWorkspaceShortcutHandler()
            .ShouldApplyTo(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(relatedWorkspace);
        Assert.False(applies);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_WrongShortcut_ReturnsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var workspaceId = new EntityId(Guid.NewGuid());
        var workspace = await SeedEntityAsync(viewModel, workspaceId, "workspace");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship("related", new EntityId(Guid.NewGuid()), sourceId: null, workspaceId));

        var applies = await new OpenAssociatedWorkspaceShortcutHandler()
            .ShouldApplyTo(viewModel, Shortcut.Open, source);

        Assert.NotNull(workspace);
        Assert.False(applies);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_NonRelatedRelationshipTypeToWorkspace_ReturnsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var workspaceId = new EntityId(Guid.NewGuid());
        var workspace = await SeedEntityAsync(viewModel, workspaceId, "workspace");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship("assigned-to", new EntityId(Guid.NewGuid()), sourceId: null, workspaceId));

        var applies = await new OpenAssociatedWorkspaceShortcutHandler()
            .ShouldApplyTo(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(workspace);
        Assert.False(applies);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_SingleWorkspace_OpensIt()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var workspaceId = new EntityId(Guid.NewGuid());
        var workspace = await SeedEntityAsync(viewModel, workspaceId, "workspace");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship("related", new EntityId(Guid.NewGuid()), sourceId: null, workspaceId));

        var handled = await new OpenAssociatedWorkspaceShortcutHandler()
            .Handle(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(workspace);
        Assert.True(handled);
        Assert.Equal(workspaceId, viewModel.SelectedWorkspacePane?.Entity.EntityId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_MultipleWorkspaces_PrefersAlreadyOpenOne()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var firstWorkspaceId = new EntityId(Guid.NewGuid());
        var secondWorkspaceId = new EntityId(Guid.NewGuid());
        var firstWorkspace = await SeedEntityAsync(viewModel, firstWorkspaceId, "workspace");
        var secondWorkspace = await SeedEntityAsync(viewModel, secondWorkspaceId, "workspace");
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = secondWorkspaceId });
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship(
                "related",
                new EntityId(Guid.NewGuid()),
                sourceId: null,
                firstWorkspaceId,
                secondWorkspaceId));

        var handled = await new OpenAssociatedWorkspaceShortcutHandler()
            .Handle(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(firstWorkspace);
        Assert.NotNull(secondWorkspace);
        Assert.True(handled);
        Assert.Equal(secondWorkspaceId, viewModel.SelectedWorkspacePane?.Entity.EntityId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_MultipleClosedWorkspaces_PicksFirstEncounteredDeterministically()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var firstWorkspaceId = new EntityId(Guid.NewGuid());
        var secondWorkspaceId = new EntityId(Guid.NewGuid());
        var firstWorkspace = await SeedEntityAsync(viewModel, firstWorkspaceId, "workspace");
        var secondWorkspace = await SeedEntityAsync(viewModel, secondWorkspaceId, "workspace");
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship(
                "related",
                new EntityId(Guid.NewGuid()),
                sourceId: null,
                firstWorkspaceId,
                secondWorkspaceId));

        var handled = await new OpenAssociatedWorkspaceShortcutHandler()
            .Handle(viewModel, Shortcut.OpenWorkspace, source);

        Assert.NotNull(firstWorkspace);
        Assert.NotNull(secondWorkspace);
        Assert.True(handled);
        Assert.Equal(firstWorkspaceId, viewModel.SelectedWorkspacePane?.Entity.EntityId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_NoWorkspaceFound_ReturnsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var source = CreateEntity(new EntityId(Guid.NewGuid()), "task");
        var paneCountBefore = viewModel.WorkspacePanes.Count;

        var handled = await new OpenAssociatedWorkspaceShortcutHandler()
            .Handle(viewModel, Shortcut.OpenWorkspace, source);

        Assert.False(handled);
        Assert.Equal(paneCountBefore, viewModel.WorkspacePanes.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GetRelatedWorkspaceIds_ParticipantEntityDeleted_IsSkippedNotThrown()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var deletedWorkspaceId = new EntityId(Guid.NewGuid());
        var source = CreateEntity(
            new EntityId(Guid.NewGuid()),
            "task",
            CreateRelationship("related", new EntityId(Guid.NewGuid()), sourceId: null, deletedWorkspaceId));
        var paneCountBefore = viewModel.WorkspacePanes.Count;
        var handled = true;

        var exception = await Record.ExceptionAsync(
            async () =>
            {
                handled = await new OpenAssociatedWorkspaceShortcutHandler()
                    .Handle(viewModel, Shortcut.OpenWorkspace, source);
            });

        Assert.Null(exception);
        Assert.False(handled);
        Assert.Equal(paneCountBefore, viewModel.WorkspacePanes.Count);
    }

    private static async Task<MainWindowViewModel> CreateInitializedViewModelAsync()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static Task<SubscribedEntityViewModel> SeedEntityAsync(
        MainWindowViewModel viewModel,
        EntityId entityId,
        string entityType)
    {
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var entityTypes = entityType == "entity"
            ? new[] { "entity" }
            : new[] { "entity", entityType };
        var workspaceProperties = entityType == "workspace"
            ? ""","regions": []"""
            : string.Empty;
        return MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            entityId,
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": {{JsonSerializer.Serialize(entityTypes)}},
              "display-name": { "default": "Test {{entityType}}" }
              {{workspaceProperties}}
            }
            """);
    }

    private static SubscribedEntityViewModel CreateEntity(
        EntityId entityId,
        string entityType,
        params EntitySnapshot[] relationships)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": ["entity", "{{entityType}}"],
              "display-name": { "default": "Test {{entityType}}" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = entityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = relationships,
            });
    }

    private static EntitySnapshot CreateRelationship(
        string relationshipType,
        EntityId relationshipId,
        EntityId? sourceId,
        params EntityId[] participantIds)
    {
        var allParticipantIds = sourceId is { } source
            ? participantIds.Prepend(source).ToArray()
            : participantIds;
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipId.Value}}",
              "entity-types": ["entity", "{{relationshipType}}", "relationship"],
              "participants": {
                "entities": {{JsonSerializer.Serialize(allParticipantIds.Select(static id => id.Value))}}
              }
            }
            """);
        return new EntitySnapshot
        {
            EntityId = relationshipId,
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = [],
        };
    }
}
