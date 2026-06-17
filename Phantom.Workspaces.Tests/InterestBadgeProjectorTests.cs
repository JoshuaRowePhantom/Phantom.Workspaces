using System;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class InterestBadgeProjectorTests
{
    private static InterestCatalog Catalog() => new(
    [
        new InterestTypeDefinition("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable"),
        new InterestTypeDefinition("blocked", "⛔", "○", "Blocked", "Not blocked", "Mark blocked", "Clear blocked"),
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

        var badges = InterestBadgeProjector.Project(Catalog(), entity);

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

        var badges = InterestBadgeProjector.Project(Catalog(), entity);

        Assert.All(badges, badge => Assert.False(badge.IsActive));
    }
}
