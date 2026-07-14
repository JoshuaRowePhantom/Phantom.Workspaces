using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class InterestToggleTests
{
    [PhantomAvaloniaFact]
    public async Task ToggleAsync_AddsThenRemovesTheInterestRelationship()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var taskId = new EntityId(Guid.NewGuid());
        await SeedAsync(dataAccessLayer, taskId, """{ "entity-types": ["entity", "task"], "names": [["tasks","t"]] }""");

        // Toggle on: no interest yet, so one is created.
        await InterestToggle.ToggleAsync(broker, await GetWithInterestsAsync(broker, taskId, ct), "actionable", ct);

        var afterAdd = await GetWithInterestsAsync(broker, taskId, ct);
        Assert.Contains(afterAdd.Relationships, IsActionable);

        // Toggle off: the interest exists, so it is removed.
        await InterestToggle.ToggleAsync(broker, afterAdd, "actionable", ct);

        var afterRemove = await GetWithInterestsAsync(broker, taskId, ct);
        Assert.DoesNotContain(afterRemove.Relationships, IsActionable);
    }

    private static bool IsActionable(EntitySnapshot relationship)
        => relationship.Data is { } data
            && data.TryGetProperty("entity-types", out var types)
            && types.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String && type.GetString() == "actionable");

    private static async Task<EntitySnapshot> GetWithInterestsAsync(EntityBroker broker, EntityId entityId, System.Threading.CancellationToken ct)
    {
        var result = await broker.EntityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                        RelationshipsToReturn = [new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet(["actionable"]) }],
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
        }, System.Threading.CancellationToken.None);

        var failure = result.EntityResults.FirstOrDefault(static entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.True(failure is null, failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static error => error.Message)));
    }
}
