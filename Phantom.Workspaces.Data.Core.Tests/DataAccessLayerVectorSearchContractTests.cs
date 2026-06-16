using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Tests;

/// <summary>
/// Contract tests for semantic (vector) search via <see cref="EntityVectorQueryClause"/> that any
/// data-access layer supporting vector search can run by supplying a factory. Implementations that
/// need out-of-band setup (for example, a MongoDB vector index) override the seed/wait hooks.
/// </summary>
public abstract class DataAccessLayerVectorSearchContractTests
{
    protected abstract IDataAccessLayer CreateDataAccessLayer();

    /// <summary>
    /// Runs a vector query, allowing implementations whose vector index is eventually consistent to
    /// poll until results are available. The default runs the query once.
    /// </summary>
    protected virtual async Task<IReadOnlyList<QueryEntitySnapshot>> RunVectorQueryAsync(
        IDataAccessLayer dataAccessLayer,
        QueryRequest request)
    {
        var result = await dataAccessLayer.QueryAsync(request);
        var batch = Assert.Single(result.Batches);
        return batch.Entities.ToArray();
    }

    [Fact]
    public async Task Vector_RanksBySemanticSimilarity_AndReportsScore()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var relatedA = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "a"), "vector search over workspace entities");
        var relatedB = await AddEntityAsync(dataAccessLayer, new EntityName("notes", "b"), "vector search across workspace entities");
        await AddEntityAsync(dataAccessLayer, new EntityName("notes", "c"), "completely unrelated cooking recipe ingredients");

        var matches = await this.RunVectorQueryAsync(
            dataAccessLayer,
            BuildVectorQuery(new EntityVectorQueryClause
            {
                VectorQueryIdentifier = new QueryClauseIdentifier("vector-clause"),
                QueryText = "semantic vector search over entities",
                NumberOfCandidates = 2,
            }));

        Assert.Equal(2, matches.Count);
        var ids = matches.Select(static entity => entity.EntityId).ToHashSet();
        Assert.Contains(relatedA, ids);
        Assert.Contains(relatedB, ids);

        Assert.All(matches, entity =>
        {
            var score = Assert.Single(entity.VectorQueryScores);
            Assert.Equal(new QueryClauseIdentifier("vector-clause"), score.QueryIdentifier);
        });
    }

    [Fact]
    public async Task Vector_LimitsToNumberOfCandidates()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        for (var index = 0; index < 5; index++)
        {
            await AddEntityAsync(
                dataAccessLayer,
                new EntityName("notes", index.ToString()),
                $"workspace entity number {index} about vector search");
        }

        var matches = await this.RunVectorQueryAsync(
            dataAccessLayer,
            BuildVectorQuery(new EntityVectorQueryClause
            {
                VectorQueryIdentifier = new QueryClauseIdentifier("vector-clause"),
                QueryText = "vector search workspace entity",
                NumberOfCandidates = 3,
            }));

        Assert.True(matches.Count <= 3, $"Expected at most 3 matches, got {matches.Count}.");
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
              "entity-types": ["note"],
              "names": {{namesJson}},
              "content": { "text": {{JsonSerializer.Serialize(text)}} }
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "add vector test entity" } },
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
