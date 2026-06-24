using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Tests;

/// <summary>
/// Contract tests for the decoupled indexing APIs - <see cref="IDataAccessLayer.ProcessQueueAsync"/>,
/// <see cref="IDataAccessLayer.ComputeEmbeddingsAsync"/>, and
/// <see cref="IDataAccessLayer.UpdateEmbeddingsAsync"/> - that any implementation supporting them
/// can run by supplying a factory. Implementations whose stored embeddings are eventually consistent
/// (for example a MongoDB vector index) override <see cref="RunVectorQueryAsync"/> to poll.
/// </summary>
public abstract class DataAccessLayerQueueEmbeddingsContractTests
{
    protected abstract IDataAccessLayer CreateDataAccessLayer();

    protected virtual async Task<IReadOnlyList<QueryEntitySnapshot>> RunVectorQueryAsync(
        IDataAccessLayer dataAccessLayer,
        QueryRequest request)
    {
        var result = await dataAccessLayer.QueryAsync(request);
        return Assert.Single(result.Batches).Entities.ToArray();
    }

    [Fact]
    public async Task ProcessQueue_ReturnsEntitiesInModifiedOrder_AndAdvancesToken()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var first = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "first"), "first");
        var second = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "second"), "second");
        var third = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "third"), "third");

        var firstBatch = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest
        {
            QueueName = "vector-index",
            Count = 2,
        });

        Assert.Equal(2, firstBatch.Entities.Count);
        Assert.Equal(first, firstBatch.Entities[0].EntityId);
        Assert.Equal(second, firstBatch.Entities[1].EntityId);
        Assert.NotNull(firstBatch.Token);

        // Acknowledge the first batch by passing its token; the next batch resumes after it.
        var secondBatch = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest
        {
            QueueName = "vector-index",
            Token = firstBatch.Token,
            Count = 2,
        });

        var thirdEntity = Assert.Single(secondBatch.Entities);
        Assert.Equal(third, thirdEntity.EntityId);

        // After acknowledging the last batch, the queue is drained.
        var drained = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest
        {
            QueueName = "vector-index",
            Token = secondBatch.Token,
            Count = 2,
        });
        Assert.Empty(drained.Entities);
    }

    [Fact]
    public async Task ComputeEmbeddings_ReturnsVectorPerEntity()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        await AddEntityAsync(dataAccessLayer, new EntityName("notes", "a"), "alpha text");
        await AddEntityAsync(dataAccessLayer, new EntityName("notes", "b"), "beta text");

        var batch = await dataAccessLayer.ProcessQueueAsync(new ProcessQueueRequest
        {
            QueueName = "vector-index",
            Count = 10,
        });
        var snapshots = batch.Entities.Cast<EntitySnapshot>().ToArray();

        var computed = await dataAccessLayer.ComputeEmbeddingsAsync(new ComputeEmbeddingsRequest
        {
            Entities = snapshots,
        });

        Assert.Equal(snapshots.Length, computed.Embeddings.Count);
        Assert.All(computed.Embeddings, embedding => Assert.NotEmpty(embedding.Values));
        var embeddedIds = computed.Embeddings.Select(embedding => embedding.EntityId).ToHashSet();
        Assert.True(snapshots.Select(snapshot => snapshot.EntityId).ToHashSet().SetEquals(embeddedIds));
    }

    [Fact]
    public async Task UpdateEmbeddings_StoredVectorDrivesVectorSearch()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var target = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "target"), "target entity");
        var other = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "other"), "other entity");

        // Store orthogonal embeddings (sized to the provider's dimensions) so a query vector aligned
        // with the target ranks it first.
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

        var matches = await this.RunVectorQueryAsync(
            dataAccessLayer,
            BuildVectorQuery(new EntityVectorQueryClause
            {
                VectorQueryIdentifier = new QueryClauseIdentifier("vector-clause"),
                QueryEmbedding = targetVector,
                NumberOfCandidates = 1,
            }));

        var match = Assert.Single(matches);
        Assert.Equal(target, match.EntityId);
    }

    [Fact]
    public async Task UpdateEmbeddings_NullValuesClearsStoredEmbedding()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var target = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "target"), "target entity");

        var dimensions = await ResolveDimensionsAsync(dataAccessLayer, target);
        await dataAccessLayer.UpdateEmbeddingsAsync(new UpdateEmbeddingsRequest
        {
            Updates = [new EmbeddingUpdate { EntityId = target, Values = UnitVector(dimensions, 0) }],
        });

        // Clearing the embedding removes it; the success result is reported.
        var result = await dataAccessLayer.UpdateEmbeddingsAsync(new UpdateEmbeddingsRequest
        {
            Updates = [new EmbeddingUpdate { EntityId = target, Values = null }],
        });

        Assert.True(result.Success);
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

    private static QueryRequest BuildVectorQuery(EntityVectorQueryClause clause) => new()
    {
        Clauses =
        [
            new TopLevelQueryClause
            {
                ClauseIdentifier = clause.VectorQueryIdentifier,
                Clause = clause,
            },
        ],
    };

    protected static async Task<EntityId> AddEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName name,
        string text)
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

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "add queue/embeddings test entity" } },
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
}
