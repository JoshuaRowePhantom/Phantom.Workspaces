using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Offline.Tests;

/// <summary>
/// Verifies that deleting an entity also removes its stored embedding, so the entity no longer
/// appears in vector search. This exercises <c>InMemoryDataAccessLayer.ClearEmbeddingsForDeletedEntities</c>
/// deterministically (the MongoDB path excludes deleted entities via the always-on <c>is-deleted</c>
/// query filter, covered by <c>MongoDbQueryTranslatorTests</c>).
/// </summary>
public sealed class InMemoryDeletionClearsEmbeddingsTests
{
    [Fact]
    public async Task DeletingEntity_ClearsStoredEmbedding_AndRemovesItFromVectorSearch()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var target = await AddNoteAsync(dataAccessLayer, new EntityName("notes", "target"), "target entity");
        var other = await AddNoteAsync(dataAccessLayer, new EntityName("notes", "other"), "other entity");

        var dimensions = await ResolveDimensionsAsync(dataAccessLayer, target);
        var targetVector = UnitVector(dimensions, 0);
        var otherVector = UnitVector(dimensions, 1);
        await dataAccessLayer.UpdateEmbeddingsAsync(new UpdateEmbeddingsRequest
        {
            Updates =
            [
                new EmbeddingUpdate { EntityId = target, Values = targetVector },
                new EmbeddingUpdate { EntityId = other, Values = otherVector },
            ],
        });

        // Sanity: querying with the target's own vector returns the target.
        var before = await QueryVectorAsync(dataAccessLayer, targetVector, numberOfCandidates: 5);
        Assert.Contains(target, before);

        // Delete the target entity (a replace with null data).
        await DeleteAsync(dataAccessLayer, target);

        // The deleted entity must no longer appear in vector search; the surviving entity still does.
        var after = await QueryVectorAsync(dataAccessLayer, targetVector, numberOfCandidates: 5);
        Assert.DoesNotContain(target, after);
        Assert.Contains(other, after);
    }

    private static async Task<IReadOnlyList<EntityId>> QueryVectorAsync(
        IDataAccessLayer dataAccessLayer,
        float[] queryVector,
        int numberOfCandidates)
    {
        var request = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("vector-clause"),
                    Clause = new EntityVectorQueryClause
                    {
                        VectorQueryIdentifier = new QueryClauseIdentifier("vector-clause"),
                        QueryEmbedding = queryVector,
                        NumberOfCandidates = numberOfCandidates,
                    },
                },
            ],
        };

        var result = await dataAccessLayer.QueryAsync(request);
        return result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.EntityId).ToArray();
    }

    private static async Task<int> ResolveDimensionsAsync(IDataAccessLayer dataAccessLayer, EntityId entityId)
    {
        var snapshot = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
            Timestamps = [null],
        });
        var entity = snapshot.Batches.SelectMany(batch => batch.Entities).First();
        var computed = await dataAccessLayer.ComputeEmbeddingsAsync(new ComputeEmbeddingsRequest
        {
            Entities = [entity],
        });
        return computed.Embeddings[0].Values.Count;
    }

    private static float[] UnitVector(int dimensions, int hotIndex)
    {
        var vector = new float[dimensions];
        vector[hotIndex] = 1f;
        return vector;
    }

    private static async Task<EntityId> AddNoteAsync(IDataAccessLayer dataAccessLayer, EntityName name, string text)
    {
        var guid = Guid.NewGuid();
        var namesJson = JsonSerializer.Serialize(new[] { name.Components });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": ["entity", "note"],
              "names": {{namesJson}},
              "content": { "text": {{JsonSerializer.Serialize(text)}} }
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "add deletion-embedding test entity" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(guid),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        Assert.DoesNotContain(result.EntityResults, static entityResult => entityResult.UpdateState == UpdateState.Failed);
        return new EntityId(guid);
    }

    private static async Task DeleteAsync(IDataAccessLayer dataAccessLayer, EntityId entityId)
    {
        var snapshot = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
            Timestamps = [null],
        });
        var entity = snapshot.Batches.SelectMany(batch => batch.Entities).First();

        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "delete entity" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = entity.ConcurrencyTag,
                    Data = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        Assert.DoesNotContain(result.EntityResults, static entityResult => entityResult.UpdateState == UpdateState.Failed);
    }
}
