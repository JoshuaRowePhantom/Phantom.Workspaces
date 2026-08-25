using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Xunit;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class InMemoryQueryEvaluatorTests
{
    /// <summary>
    /// #1360: A Top clause carrying sort specifications orders matched entities by the requested field
    /// BEFORE taking the top-N, so the limit selects the highest-ordered entities rather than an
    /// arbitrary subset.
    /// </summary>
    [Fact]
    public async Task InMemoryQuery_WithSortAndLimit_OrdersByFieldBeforeTakingTopN()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        // Insert in shuffled order so a correct top-N cannot be an artifact of insertion order.
        foreach (var rank in new[] { 2, 0, 4, 1, 3 })
        {
            await AddRankedNoteAsync(dataAccessLayer, rank);
        }

        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("top"),
                    Clause = new TopQueryClause
                    {
                        ResultLimit = new QueryResultLimit(2),
                        SortSpecifications =
                        [
                            new SortSpecification
                            {
                                FieldPath = new FieldPath("rank"),
                                Direction = SortDirection.Descending,
                            },
                        ],
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                    },
                },
            ],
        });

        var entities = Assert.Single(result.Batches).Entities;
        Assert.Equal(2, entities.Count);
        var ranks = entities.Select(entity => entity.Data!.Value.GetProperty("rank").GetInt32()).ToHashSet();
        Assert.Equal(new HashSet<int> { 4, 3 }, ranks);
    }

    /// <summary>
    /// #1360: An ascending sort returns the lowest-ordered entities within the limit.
    /// </summary>
    [Fact]
    public async Task InMemoryQuery_WithAscendingSortAndLimit_TakesLowestOrderedTopN()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        foreach (var rank in new[] { 2, 0, 4, 1, 3 })
        {
            await AddRankedNoteAsync(dataAccessLayer, rank);
        }

        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("top"),
                    Clause = new TopQueryClause
                    {
                        ResultLimit = new QueryResultLimit(2),
                        SortSpecifications =
                        [
                            new SortSpecification
                            {
                                FieldPath = new FieldPath("rank"),
                                Direction = SortDirection.Ascending,
                            },
                        ],
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                    },
                },
            ],
        });

        var entities = Assert.Single(result.Batches).Entities;
        Assert.Equal(2, entities.Count);
        var ranks = entities.Select(entity => entity.Data!.Value.GetProperty("rank").GetInt32()).ToHashSet();
        Assert.Equal(new HashSet<int> { 0, 1 }, ranks);
    }

    private static async Task AddRankedNoteAsync(IDataAccessLayer dataAccessLayer, int rank)
    {
        var guid = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": ["note"],
              "names": [["notes", "{{rank}}"]],
              "rank": {{rank}}
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed ranked note" } },
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

        Assert.DoesNotContain(result.EntityResults, entityResult => entityResult.UpdateState == UpdateState.Failed);
    }
}
