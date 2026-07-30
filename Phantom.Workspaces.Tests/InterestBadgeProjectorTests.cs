using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class InterestBadgeProjectorTests
{
    private static readonly EntityId User = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly EntityId Profile = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));

    private static IReadOnlyList<InterestAppliesTo> UserApplies =>
        [new InterestAppliesTo("user", new HashSet<string> { "user" }, InterestSessionValue.UserEntityId)];

    private static IReadOnlyList<InterestAppliesTo> ProfileApplies =>
        [new InterestAppliesTo("applied-to", new HashSet<string> { "user-computer-profile" }, InterestSessionValue.UserComputerProfileEntityId)];

    private static InterestTypeDefinition Actionable(IReadOnlySet<string>? display = null)
        => new("actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", display,
            TargetParticipant: "target", AppliesTo: UserApplies);

    private static InterestTypeDefinition Blocked(IReadOnlySet<string>? display = null)
        => new("blocked", "⛔", "○", "Blocked", "Not blocked", "Mark blocked", "Clear blocked", display,
            TargetParticipant: "target", AppliesTo: UserApplies);

    private static InterestTypeDefinition DefaultInterest(IReadOnlySet<string>? display = null)
        => new("default", "⭐", "☆", "Default workspace", "Not the default workspace", "Make default", "Clear default", display ?? new HashSet<string> { "workspace" },
            TargetParticipant: "value", AppliesTo: ProfileApplies);

    private static InterestCatalog TwoInterestCatalog() => new([Actionable(), Blocked()]);

    private static EntityTypeCatalog TaskTypeCatalog() => new([new EntityTypeDefinition("task", new HashSet<string>())]);

    private static EntitySnapshot Entity(EntityId entityId, string entityType = "task", params EntitySnapshot[] relationships) => new()
    {
        EntityId = entityId,
        ConcurrencyTag = new ConcurrencyTag("1"),
        ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
        Data = JsonDocument.Parse($$"""{ "entity-id": "{{entityId.Value}}", "entity-types": ["entity", "{{entityType}}"] }""").RootElement.Clone(),
        Relationships = relationships,
    };

    private static EntitySnapshot StandardRelationship(string interestType, EntityId target, EntityId user, string? note = null)
    {
        var noteJson = note is null ? string.Empty : $", \"note\": {JsonSerializer.Serialize(note)}";
        return new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""{ "entity-types": ["entity", "{{interestType}}","relationship"], "participants": { "target": "{{target.Value}}", "user": "{{user.Value}}" }{{noteJson}} }""").RootElement.Clone(),
            Relationships = [],
        };
    }

    private static EntitySnapshot DefaultRelationship(EntityId workspaceId, EntityId profileId, string? note = null)
    {
        var noteJson = note is null ? string.Empty : $", \"note\": {JsonSerializer.Serialize(note)}";
        return new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""{ "entity-types": ["entity", "default","relationship"], "participants": { "applied-to": "{{profileId.Value}}", "value": "{{workspaceId.Value}}" }{{noteJson}} }""").RootElement.Clone(),
            Relationships = [],
        };
    }

    [Fact]
    public void Project_MarksAppliedInterestActive_AndOthersInactive()
    {
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId, "task", StandardRelationship("actionable", entityId, User, note: "Needs your review"));

        var badges = InterestBadgeProjector.Project(TwoInterestCatalog(), TaskTypeCatalog(), entity, User, Profile);

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
        var entity = Entity(entityId, "task", StandardRelationship("actionable", otherId, User));

        var badges = InterestBadgeProjector.Project(TwoInterestCatalog(), TaskTypeCatalog(), entity, User, Profile);

        Assert.All(badges, badge => Assert.False(badge.IsActive));
    }

    [Fact]
    public void Project_FiltersBasedOnDisplayEntityTypes()
    {
        var interestCatalog = new InterestCatalog(
        [
            Actionable(new HashSet<string> { "task" }),
            Blocked(new HashSet<string> { "note" }),
        ]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("task", new HashSet<string>())]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    [Fact]
    public void Project_FiltersBasedOnDisplayInterestTypes()
    {
        var interestCatalog = new InterestCatalog([Actionable(), Blocked()]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("task", new HashSet<string> { "actionable" })]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    [Fact]
    public void Project_NullDisplayEntityTypes_ShowsOnAllEntityTypes()
    {
        var interestCatalog = new InterestCatalog([Actionable()]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("task", new HashSet<string>()),
            new EntityTypeDefinition("note", new HashSet<string>()),
        ]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    [Fact]
    public void Project_EmptyDisplayEntityTypes_OnlyShowsIfEntityTypeRequestsIt()
    {
        var interestCatalog = new InterestCatalog([Actionable(new HashSet<string>())]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("task", new HashSet<string>())]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        Assert.Empty(badges);
    }

    [Fact]
    public void Project_EmptyDisplayEntityTypes_ShowsWhenEntityTypeRequestsIt()
    {
        var interestCatalog = new InterestCatalog([Actionable(new HashSet<string>())]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("task", new HashSet<string> { "actionable" })]);
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId);

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.Equal("actionable", badge.InterestTypeName);
    }

    // --- New tests for issue #1137 ---

    [Fact]
    public void Project_DefaultInterest_ShowsOnWorkspaceEntities()
    {
        var interestCatalog = new InterestCatalog([DefaultInterest()]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("workspace", new HashSet<string>())]);
        var workspaceId = new EntityId(Guid.NewGuid());
        var entity = Entity(workspaceId, "workspace");

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.Equal("default", badge.InterestTypeName);
    }

    [Fact]
    public void Project_DefaultInterest_DoesNotShowOnNonWorkspaceEntities()
    {
        var interestCatalog = new InterestCatalog([DefaultInterest()]);
        var entityTypeCatalog = new EntityTypeCatalog(
        [
            new EntityTypeDefinition("workspace", new HashSet<string>()),
            new EntityTypeDefinition("task", new HashSet<string>()),
        ]);
        var taskId = new EntityId(Guid.NewGuid());
        var entity = Entity(taskId, "task");

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        Assert.Empty(badges);
    }

    [Fact]
    public void Project_ConfiguredTargetParticipant_MarksActiveWhenValueMatchesEntity()
    {
        var interestCatalog = new InterestCatalog([DefaultInterest()]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("workspace", new HashSet<string>())]);
        var workspaceId = new EntityId(Guid.NewGuid());
        var entity = Entity(workspaceId, "workspace", DefaultRelationship(workspaceId, Profile));

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.Equal("default", badge.InterestTypeName);
        Assert.True(badge.IsActive);
        Assert.Equal("⭐", badge.Glyph);
    }

    [Fact]
    public void Project_AppliesToScope_MarksInactiveWhenAppliedToIsAnotherProfile()
    {
        var interestCatalog = new InterestCatalog([DefaultInterest()]);
        var entityTypeCatalog = new EntityTypeCatalog([new EntityTypeDefinition("workspace", new HashSet<string>())]);
        var otherProfile = new EntityId(Guid.NewGuid());
        var workspaceId = new EntityId(Guid.NewGuid());
        var entity = Entity(workspaceId, "workspace", DefaultRelationship(workspaceId, otherProfile));

        var badges = InterestBadgeProjector.Project(interestCatalog, entityTypeCatalog, entity, User, Profile);

        var badge = Assert.Single(badges);
        Assert.False(badge.IsActive);
        Assert.Equal("☆", badge.Glyph);
    }

    [Fact]
    public void Project_StandardInterest_MarksInactiveWhenUserParticipantIsAnotherUser()
    {
        var otherUser = new EntityId(Guid.NewGuid());
        var entityId = new EntityId(Guid.NewGuid());
        var entity = Entity(entityId, "task", StandardRelationship("actionable", entityId, otherUser));

        var badges = InterestBadgeProjector.Project(TwoInterestCatalog(), TaskTypeCatalog(), entity, User, Profile);

        var actionable = Assert.Single(badges, b => b.InterestTypeName == "actionable");
        Assert.False(actionable.IsActive);
    }
}
