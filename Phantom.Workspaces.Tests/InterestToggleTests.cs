using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class InterestToggleTests
{
    private static readonly InterestTypeDefinition StandardActionable = new(
        "actionable", "❗", "○", "Actionable", "Not actionable", "Mark actionable", "Clear actionable", null,
        TargetParticipant: "target",
        AppliesTo: [new InterestAppliesTo("user", new HashSet<string> { "user" }, InterestSessionValue.UserEntityId)]);

    private static readonly InterestTypeDefinition DefaultInterest = new(
        "default", "⭐", "☆", "Default workspace", "Not default", "Make default", "Clear default", new HashSet<string> { "workspace" },
        TargetParticipant: "value",
        AppliesTo: [new InterestAppliesTo("applied-to", new HashSet<string> { "user-computer-profile" }, InterestSessionValue.UserComputerProfileEntityId)]);

    [AvaloniaFact]
    public async Task ToggleAsync_AddsThenRemovesTheInterestRelationship()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var taskId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, taskId, """{ "entity-types": ["entity", "task"], "names": [["tasks","t"]] }""");

        await InterestToggle.ToggleAsync(broker, await GetWithInterestsAsync(broker, taskId, "actionable", ct), StandardActionable, ct);

        var afterAdd = await GetWithInterestsAsync(broker, taskId, "actionable", ct);
        Assert.Contains(afterAdd.Relationships, r => HasEntityType(r, "actionable"));

        await InterestToggle.ToggleAsync(broker, afterAdd, StandardActionable, ct);

        var afterRemove = await GetWithInterestsAsync(broker, taskId, "actionable", ct);
        Assert.DoesNotContain(afterRemove.Relationships, r => HasEntityType(r, "actionable"));
    }

    [AvaloniaFact]
    public async Task ToggleAsync_DefaultInterest_WhenNotDefault_CreatesRelationshipWithValueAndAppliedToParticipants()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var profileId = broker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, workspaceId, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","w"]] }""");

        await InterestToggle.ToggleAsync(broker, await GetWithInterestsAsync(broker, workspaceId, "default", ct), DefaultInterest, ct);

        var after = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        var defaultRel = Assert.Single(after.Relationships, r => HasEntityType(r, "default"));
        var data = defaultRel.Data!.Value;
        var participants = data.GetProperty("participants");
        Assert.Equal(workspaceId.Value.ToString(), participants.GetProperty("value").GetString());
        Assert.Equal(profileId.Value.ToString(), participants.GetProperty("applied-to").GetString());
    }

    [AvaloniaFact]
    public async Task ToggleAsync_DefaultInterest_WhenAlreadyDefault_RemovesRelationship()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var profileId = broker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, workspaceId, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","w"]] }""");
        var existingDefaultId = new EntityId(Guid.NewGuid());
        await SeedDefaultAsync(dataAccessLayer, existingDefaultId, workspaceId, profileId);

        var withDefault = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        Assert.Contains(withDefault.Relationships, r => HasEntityType(r, "default"));

        await InterestToggle.ToggleAsync(broker, withDefault, DefaultInterest, ct);

        var after = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        Assert.DoesNotContain(after.Relationships, r => HasEntityType(r, "default"));
    }

    [AvaloniaFact]
    public async Task ToggleAsync_DefaultInterest_WhenAnotherWorkspaceIsDefaultForSameProfile_LeavesExistingDefaultIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var profileId = broker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var firstWorkspaceId = new EntityId(Guid.NewGuid());
        var secondWorkspaceId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, firstWorkspaceId, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","first"]] }""");
        await SeedAsync(dataAccessLayer, secondWorkspaceId, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","second"]] }""");

        var firstDefaultId = new EntityId(Guid.NewGuid());
        await SeedDefaultAsync(dataAccessLayer, firstDefaultId, firstWorkspaceId, profileId);

        var secondSnapshot = await GetWithInterestsAsync(broker, secondWorkspaceId, "default", ct);
        await InterestToggle.ToggleAsync(broker, secondSnapshot, DefaultInterest, ct);

        var firstAfter = await GetWithInterestsAsync(broker, firstWorkspaceId, "default", ct);
        Assert.Contains(firstAfter.Relationships, r =>
            HasEntityType(r, "default") && r.EntityId == firstDefaultId);

        var secondAfter = await GetWithInterestsAsync(broker, secondWorkspaceId, "default", ct);
        Assert.Contains(secondAfter.Relationships, r => HasEntityType(r, "default"));
    }

    [AvaloniaFact]
    public async Task ToggleAsync_DefaultInterest_WhenDefaultExistsForAnotherProfile_LeavesOtherProfileDefaultIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var currentProfileId = broker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, workspaceId, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","w"]] }""");

        var otherProfileId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, otherProfileId, """{ "entity-types": ["entity", "workspace"], "names": [["profiles","other-profile-stand-in"]] }""");
        var otherProfileDefaultId = new EntityId(Guid.NewGuid());
        await SeedDefaultAsync(dataAccessLayer, otherProfileDefaultId, workspaceId, otherProfileId);

        // Toggle for the current profile creates a new default; the other profile's default is untouched.
        var snapshot = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        await InterestToggle.ToggleAsync(broker, snapshot, DefaultInterest, ct);

        var after = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        Assert.Contains(after.Relationships, r =>
            HasEntityType(r, "default") && r.EntityId == otherProfileDefaultId);
        Assert.Contains(after.Relationships, r =>
            HasEntityType(r, "default") && ReadParticipant(r, "applied-to") == currentProfileId.Value.ToString());

        // Toggle off with a fresh snapshot should only remove the current profile's default; the other profile's remains.
        var withBoth = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        await InterestToggle.ToggleAsync(broker, withBoth, DefaultInterest, ct);

        var afterOff = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        var remaining = Assert.Single(afterOff.Relationships, r => HasEntityType(r, "default"));
        Assert.Equal(otherProfileDefaultId, remaining.EntityId);
        Assert.Equal(otherProfileId.Value.ToString(), ReadParticipant(remaining, "applied-to"));
    }

    [AvaloniaFact]
    public async Task ToggleAsync_DefaultInterest_AddsThenRemovesTheDefaultRelationship()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var workspaceId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, workspaceId, """{ "entity-types": ["entity", "workspace"], "names": [["workspaces","w"]] }""");

        await InterestToggle.ToggleAsync(broker, await GetWithInterestsAsync(broker, workspaceId, "default", ct), DefaultInterest, ct);
        var afterOn = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        Assert.Contains(afterOn.Relationships, r => HasEntityType(r, "default"));

        await InterestToggle.ToggleAsync(broker, afterOn, DefaultInterest, ct);
        var afterOff = await GetWithInterestsAsync(broker, workspaceId, "default", ct);
        Assert.DoesNotContain(afterOff.Relationships, r => HasEntityType(r, "default"));
    }

    [AvaloniaFact]
    public async Task ToggleAsync_StandardInterest_StillWritesTargetAndUserParticipants()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var userId = broker.EntityRepository.WorkspaceEntitySession.UserEntityId;

        var taskId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, taskId, """{ "entity-types": ["entity", "task"], "names": [["tasks","t"]] }""");

        await InterestToggle.ToggleAsync(broker, await GetWithInterestsAsync(broker, taskId, "actionable", ct), StandardActionable, ct);

        var after = await GetWithInterestsAsync(broker, taskId, "actionable", ct);
        var rel = Assert.Single(after.Relationships, r => HasEntityType(r, "actionable"));
        var participants = rel.Data!.Value.GetProperty("participants");
        Assert.Equal(taskId.Value.ToString(), participants.GetProperty("target").GetString());
        Assert.Equal(userId.Value.ToString(), participants.GetProperty("user").GetString());
    }

    private static bool HasEntityType(EntitySnapshot relationship, string entityType)
        => relationship.Data is { } data
            && data.TryGetProperty("entity-types", out var types)
            && types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == entityType);

    private static string? ReadParticipant(EntitySnapshot relationship, string participantName)
    {
        if (relationship.Data is not { } data
            || !data.TryGetProperty("participants", out var participants)
            || !participants.TryGetProperty(participantName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static async Task<EntitySnapshot> GetWithInterestsAsync(EntityBroker broker, EntityId entityId, string interestTypeName, CancellationToken ct)
    {
        var result = await broker.EntityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                        RelationshipsToReturn = [new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet([interestTypeName]) }],
                    },
                ],
            },
            ct);
        return result.Batches.SelectMany(batch => batch.Entities).Single(entity => entity.EntityId == entityId);
    }

    private static async Task SeedAsync(IDataAccessLayer dataAccessLayer, EntityId id, string bodyJson)
    {
        using var body = JsonDocument.Parse(bodyJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", id.Value);
            foreach (var property in body.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
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
        }, CancellationToken.None);

        var failure = result.EntityResults.FirstOrDefault(static entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.True(failure is null, failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static error => error.Message)));
    }

    private static Task SeedDefaultAsync(IDataAccessLayer dataAccessLayer, EntityId relationshipId, EntityId workspaceId, EntityId profileId)
        => SeedAsync(
            dataAccessLayer,
            relationshipId,
            $$"""
            {
              "entity-types": ["entity", "default", "relationship"],
              "names": [["relationships","default-{{relationshipId.Value}}"]],
              "participants": { "applied-to": "{{profileId.Value}}", "value": "{{workspaceId.Value}}" }
            }
            """);
}
