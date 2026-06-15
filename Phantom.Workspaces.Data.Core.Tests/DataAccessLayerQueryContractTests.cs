using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Tests;

/// <summary>
/// Contract tests for <see cref="IDataAccessLayer.QueryAsync"/> that any implementation supporting
/// query-clause evaluation (and vector search) can run by supplying a data-access layer factory.
/// Covers entity-type, full-text and vector clauses plus And/Or/Not/Top composition.
/// </summary>
public abstract class DataAccessLayerQueryContractTests
{
    protected abstract IDataAccessLayer CreateDataAccessLayer();

    [Fact]
    public async Task Query_EntityType_ReturnsOnlyMatchingType()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var noteId = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "one"), "first note");
        await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "one"), "first task");

        var matches = await QueryAsync(
            dataAccessLayer,
            "by-type",
            new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) });

        var match = Assert.Single(matches);
        Assert.Equal(noteId, match.EntityId);
        Assert.Contains(new QueryClauseIdentifier("by-type"), match.MatchingClauseIdentifiers);
    }

    [Fact]
    public async Task Query_FullText_MatchesAndReportsScore()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var matchId = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "alpha"), "the quick brown fox");
        await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "beta"), "lazy sleeping dog");

        var matches = await QueryAsync(
            dataAccessLayer,
            "by-text",
            new EntityFullTextQueryClause
            {
                FullTextQueryIdentifier = new QueryClauseIdentifier("text-clause"),
                QueryText = new FullTextQueryText("brown"),
            });

        var match = Assert.Single(matches);
        Assert.Equal(matchId, match.EntityId);
        var score = Assert.Single(match.FullTextQueryScores);
        Assert.Equal(new QueryClauseIdentifier("text-clause"), score.QueryIdentifier);
        Assert.True(score.Score > 0);
    }

    [Fact]
    public async Task Query_Vector_RanksBySemanticSimilarityAndReportsScore()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var relatedA = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "a"), "vector search over workspace entities");
        var relatedB = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "b"), "vector search across workspace entities");
        await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "c"), "completely unrelated cooking recipe ingredients");

        var matches = await QueryAsync(
            dataAccessLayer,
            "by-vector",
            new EntityVectorQueryClause
            {
                VectorQueryIdentifier = new QueryClauseIdentifier("vector-clause"),
                QueryText = "semantic vector search over entities",
                NumberOfCandidates = 2,
            });

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
    public async Task Query_And_IntersectsClauses()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var bothId = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "both"), "important meeting notes");
        await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "typeonly"), "unrelated text");
        await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "textonly"), "important meeting notes");

        var matches = await QueryAsync(
            dataAccessLayer,
            "and",
            new AndQueryClause
            {
                Clauses =
                [
                    new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                    new EntityFullTextQueryClause
                    {
                        FullTextQueryIdentifier = new QueryClauseIdentifier("text-clause"),
                        QueryText = new FullTextQueryText("meeting"),
                    },
                ],
            });

        var match = Assert.Single(matches);
        Assert.Equal(bothId, match.EntityId);
    }

    [Fact]
    public async Task Query_Or_UnionsClauses()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var noteId = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "one"), "alpha");
        var taskId = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "one"), "beta");
        await AddEntityAsync(dataAccessLayer, ["folder"], new EntityName("folders", "one"), "gamma");

        var matches = await QueryAsync(
            dataAccessLayer,
            "or",
            new OrQueryClause
            {
                Clauses =
                [
                    new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                    new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["task"]) },
                ],
            });

        var ids = matches.Select(static entity => entity.EntityId).ToHashSet();
        Assert.Equal(2, ids.Count);
        Assert.Contains(noteId, ids);
        Assert.Contains(taskId, ids);
    }

    [Fact]
    public async Task Query_Not_ExcludesClause()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "one"), "alpha");
        var taskId = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "one"), "beta");

        var matches = await QueryAsync(
            dataAccessLayer,
            "not",
            new NotQueryClause
            {
                Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
            });

        var match = Assert.Single(matches);
        Assert.Equal(taskId, match.EntityId);
    }

    [Fact]
    public async Task Query_Top_LimitsResults()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        for (var index = 0; index < 5; index++)
        {
            await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", index.ToString()), "shared keyword text");
        }

        var matches = await QueryAsync(
            dataAccessLayer,
            "top",
            new TopQueryClause
            {
                ResultLimit = new QueryResultLimit(2),
                Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
            });

        Assert.Equal(2, matches.Count);
    }

    protected static async Task<EntityId> AddEntityAsync(
        IDataAccessLayer dataAccessLayer,
        IReadOnlyList<string> entityTypes,
        EntityName name,
        string? text = null)
    {
        var guid = Guid.NewGuid();
        var entityId = new EntityId(guid);
        var entityTypesJson = JsonSerializer.Serialize(entityTypes);
        var namesJson = JsonSerializer.Serialize(new[] { name.Components });
        var contentJson = text is null
            ? string.Empty
            : $",\"content\":{{\"text\":{JsonSerializer.Serialize(text)}}}";

        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": {{entityTypesJson}},
              "names": {{namesJson}}{{contentJson}}
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "add query test entity" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = null,
                        Data = document.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        Assert.DoesNotContain(result.EntityResults, static entityResult => entityResult.UpdateState == UpdateState.Failed);
        return entityId;
    }

    protected static async Task<IReadOnlyList<QueryEntitySnapshot>> QueryAsync(
        IDataAccessLayer dataAccessLayer,
        string clauseIdentifier,
        QueryClause clause)
    {
        var result = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier(clauseIdentifier),
                        Clause = clause,
                    },
                ],
            });

        var batch = Assert.Single(result.Batches);
        return batch.Entities.ToArray();
    }
}
