using System;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class InterestBadgeProjectorTests
{
    private static InterestCatalog InterestCatalog() => new(
    [
        new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", null),
        new InterestTypeDefinition("blocked", "⛔", "○", "Blocked", "Not blocked", "Mark blocked", "Clear blocked", null),
    ]);

    private static EntityTypeCatalog EntityTypeCatalog() => new(
    [
        new EntityTypeDefinition("task", new HashSet<string>()),
    ]);

    private static EntitySnapshot Entity(EntityId entityId, params EntitySnapshot[] relationships) => new()
    {
        EntityId = entityId,
        ConcurrencyTag = new ConcurrencyTag("1"),
        ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
        Data = JsonDocument.Parse($$"""{ "entity-id": "{{entityId.Value}}", "entity-types": ["task"] }""").RootElement.Clone(),
        Relationships = relationships,
    };

    private static EntitySnapshot Relationship(string interestType, EntityId target, string? note = null)
    {
        var noteJson = note is null ? string.Empty : $", \"note\": {JsonSerializer.Serialize(note)}";
        return new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""{ "entity-types": ["{{interestType}}","relationship"], "participants": { "target": "{{target.Value}}" }{{noteJson}} }""").RootElement.Clone(),
            Relationships = [],
        };
    }

    [Fact]
    public void Project_MarksAppliedInterestActive_AndOthersInactive()
    {
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId, Relationship("actionable", entityId, note: "Needs your review"));

        var badges = InterestBadgeProjector.Project(InterestCatalog(), EntityTypeCatalog(), entity);

        var actionable = Assert.Single(badges, badge => badge.InterestTypeName == "actionable");
        Assert.True(actionable.IsActive);
        Assert.Equal("❗", actionable.Glyph);
        Assert.Contains("Needs your review", actionable.Tooltip);

        var blocked = Assert.Single(badges, badge => badge.InterestTypeName == "blocked");
        Assert.False(blocked.IsActive);
        Assert.Equal("○", blocked.Glyph);
    }

    [Fact]
    public void Project_IgnoresRelationshipsWhereEntityIsNotTarget()
    {
        var entityId = new EntityId(Guid.NewGuid());
        var otherId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId, Relationship("actionable", otherId));

        var badges = InterestBadgeProjector.Project(InterestCatalog(), EntityTypeCatalog(), entity);

        Assert.All(badges, badge => Assert.False(badge.IsActive));
    }

    [Fact]
    public void Project_FiltersBasedOnDisplayEntityTypes()
    {
        var interestCatalog = new InterestCatalog(
        [
            new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", new HashSet<string> { "task" }),
            new InterestTypeDefinition("blocked", "⛔", "○", "Blocked", "Not blocked", "Mark blocked", "Clear blocked", new HashSet<string> { "note" }),
        ]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("task", new HashSet<string>()),
        ]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity);

        // Only actionable should be shown (task entity type, actionable allows task)
        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    [Fact]
    public void Project_FiltersBasedOnDisplayInterestTypes()
    {
        var interestCatalog = new InterestCatalog(
        [
            new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", null),
            new InterestTypeDefinition("blocked", "⛔", "○", "Blocked", "Not blocked", "Mark blocked", "Clear blocked", null),
        ]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("task", new HashSet<string> { "actionable" }),
        ]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity);

        // Only actionable should be shown (task entity type specifies actionable)
        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    [Fact]
    public void Project_NullDisplayEntityTypes_ShowsOnAllEntityTypes()
    {
        var interestCatalog = new InterestCatalog(
        [
            new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", null),
        ]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("task", new HashSet<string>()),
            new EntityTypeDefinition("note", new HashSet<string>()),
        ]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity);

        // Null display-entity-types means show on all entity types
        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    [Fact]
    public void Project_EmptyDisplayEntityTypes_OnlyShowsIfEntityTypeRequestsIt()
    {
        var interestCatalog = new InterestCatalog(
        [
            new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", new HashSet<string>()),
        ]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("task", new HashSet<string>()), // Does NOT request actionable
        ]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity);

        // Empty display-entity-types means only show if entity type explicitly requests it
        Assert.Empty(badges);
    }

    [Fact]
    public void Project_EmptyDisplayEntityTypes_ShowsWhenEntityTypeRequestsIt()
    {
        var interestCatalog = new InterestCatalog(
        [
            new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", new HashSet<string>()),
        ]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("task", new HashSet<string> { "actionable" }), // DOES request actionable
        ]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity);

        // Empty display-entity-types, but entity type requests it
        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }
}
