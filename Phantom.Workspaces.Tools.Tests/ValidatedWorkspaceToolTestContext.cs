using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Tools.Tests;

internal static class ValidatedWorkspaceToolTestContext
{
    public static async Task<WorkspaceToolExecutionContext> CreateAsync(
        ValidatingEntitySeedFixture fixture,
        params JsonElement[] participantEntities)
    {
        var contextEntityId = new EntityId();
        var contextEntity = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{contextEntityId}}",
              "entity-types": ["entity", "task"],
              "names": [["tasks", "workspace-tool-test-context"]]
            }
            """).RootElement.Clone();
        await fixture.SeedManyValidAsync([contextEntity, .. participantEntities]);

        var participantIds = participantEntities
            .Select(static entity => new EntityId(entity.GetProperty("entity-id").GetString()!))
            .ToArray();
        var result = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest { EntityId = contextEntityId },
                    .. participantIds.Select(static id => new GetEntityRequest { EntityId = id }),
                ],
                Timestamps = [null],
            });
        var snapshots = result.Batches.SelectMany(static batch => batch.Entities).ToArray();
        var contextSnapshot = snapshots.Single(snapshot => snapshot.EntityId == contextEntityId);
        var participants = participantIds
            .Select(id => snapshots.Single(snapshot => snapshot.EntityId == id))
            .ToArray();

        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = fixture.DataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = contextSnapshot,
            CurrentUserEntity = contextSnapshot,
            CurrentComputerUserProfileEntity = contextSnapshot,
            ToolRelationship = contextSnapshot,
            Participants = participants,
            Tool = contextSnapshot,
            Schedule = contextSnapshot,
        };
    }
}
