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
/// Covers entity-type and vector clauses plus And/Or/Not/Top composition.
/// </summary>
public abstract class DataAccessLayerQueryContractTests
{
    protected abstract IDataAccessLayer CreateDataAccessLayer();

    /// <summary>
    /// Runs a query, allowing implementations whose search index is eventually consistent (for example
    /// a MongoDB Atlas vector index) to poll until results are available. The default runs once.
    /// </summary>
    protected virtual async Task<IReadOnlyList<QueryEntitySnapshot>> RunVectorQueryAsync(
        IDataAccessLayer dataAccessLayer,
        QueryRequest request)
    {
        var result = await dataAccessLayer.QueryAsync(request);
        return Assert.Single(result.Batches).Entities.ToArray();
    }

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
    public async Task Query_EntityField_Equals_ReturnsOnlyMatchingEntity()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var openId = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "open"), status: "open");
        await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "closed"), status: "closed");

        var matches = await QueryAsync(
            dataAccessLayer,
            "by-field",
            new EntityFieldQueryClause
            {
                FieldPath = new FieldPath("status"),
                ComparisonOperator = FieldComparisonOperator.Equals,
                Value = JsonSerializer.SerializeToElement("open"),
            });

        Assert.Equal(openId, Assert.Single(matches).EntityId);
    }

    [Fact]
    public async Task Query_Participation_ReturnsRoleParticipants()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var task = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "p"));
        var user = await AddEntityAsync(dataAccessLayer, ["user"], new EntityName("users", "p"));
        await AddRelationshipAsync(dataAccessLayer, ["assigned-to"], new EntityName("relationships", "a"), $$"""{ "target": "{{task.Value}}", "user": "{{user.Value}}" }""");

        var ids = await QueryIdsAsync(dataAccessLayer, new EntityParticipationQueryClause
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
            ParticipationRoleNames = new RoleNameSet(["target"]),
        });

        Assert.Contains(task, ids);
        Assert.DoesNotContain(user, ids);
    }

    [Fact]
    public async Task Query_Participation_FilteredByMustHaveParticipant()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var user = await AddEntityAsync(dataAccessLayer, ["user"], new EntityName("users", "mine"));
        var otherUser = await AddEntityAsync(dataAccessLayer, ["user"], new EntityName("users", "other"));
        var mine = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "mine"));
        var theirs = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "theirs"));
        await AddRelationshipAsync(dataAccessLayer, ["assigned-to"], new EntityName("relationships", "mine"), $$"""{ "target": "{{mine.Value}}", "user": "{{user.Value}}" }""");
        await AddRelationshipAsync(dataAccessLayer, ["assigned-to"], new EntityName("relationships", "theirs"), $$"""{ "target": "{{theirs.Value}}", "user": "{{otherUser.Value}}" }""");

        var ids = await QueryIdsAsync(dataAccessLayer, AssignedToForUser(user));

        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
    }

    [Fact]
    public async Task Query_ParticipationAndNotParticipation_ExcludesExcludedTargetsByJoin()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var user = await AddEntityAsync(dataAccessLayer, ["user"], new EntityName("users", "u"));
        var visible = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "visible"));
        var hidden = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "hidden"));
        await AddRelationshipAsync(dataAccessLayer, ["assigned-to"], new EntityName("relationships", "av"), $$"""{ "target": "{{visible.Value}}", "user": "{{user.Value}}" }""");
        await AddRelationshipAsync(dataAccessLayer, ["assigned-to"], new EntityName("relationships", "ah"), $$"""{ "target": "{{hidden.Value}}", "user": "{{user.Value}}" }""");
        await AddRelationshipAsync(dataAccessLayer, ["not-interesting"], new EntityName("relationships", "ni"), $$"""{ "target": "{{hidden.Value}}" }""");

        var ids = await QueryIdsAsync(dataAccessLayer, new AndQueryClause
        {
            Clauses =
            [
                AssignedToForUser(user),
                new NotQueryClause
                {
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(["not-interesting"]),
                        ParticipationRoleNames = new RoleNameSet(["target"]),
                    },
                },
            ],
        });

        Assert.Contains(visible, ids);
        Assert.DoesNotContain(hidden, ids);
    }

    [Fact]
    public async Task Query_Vector_RanksBySemanticSimilarityAndReportsScore()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var relatedA = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "a"), "vector search over workspace entities");
        var relatedB = await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "b"), "vector search across workspace entities");
        await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "c"), "completely unrelated cooking recipe ingredients");

        var matches = await this.RunVectorQueryAsync(
            dataAccessLayer,
            BuildSingleClauseQuery(
                "by-vector",
                new EntityVectorQueryClause
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
    public async Task Query_Vector_LimitsToNumberOfCandidates()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        for (var index = 0; index < 5; index++)
        {
            await AddEntityAsync(
                dataAccessLayer,
                ["note"],
                new EntityName("notes", index.ToString()),
                $"workspace entity number {index} about vector search");
        }

        var matches = await this.RunVectorQueryAsync(
            dataAccessLayer,
            BuildSingleClauseQuery(
                "vector-clause",
                new EntityVectorQueryClause
                {
                    VectorQueryIdentifier = new QueryClauseIdentifier("vector-clause"),
                    QueryText = "vector search workspace entity",
                    NumberOfCandidates = 3,
                }));

        Assert.True(matches.Count <= 3, $"Expected at most 3 matches, got {matches.Count}.");
    }

    [Fact]
    public async Task Query_And_IntersectsClauses()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var bothId = await AddEntityAsync(dataAccessLayer, ["note", "meeting"], new EntityName("notes", "both"), "important meeting notes");
        await AddEntityAsync(dataAccessLayer, ["note"], new EntityName("notes", "noteonly"), "unrelated text");
        await AddEntityAsync(dataAccessLayer, ["meeting"], new EntityName("meetings", "meetingonly"), "important meeting notes");

        var matches = await QueryAsync(
            dataAccessLayer,
            "and",
            new AndQueryClause
            {
                Clauses =
                [
                    new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                    new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["meeting"]) },
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

    [Fact]
    public async Task Query_WithRelationshipsToReturn_PopulatesMatchedEntityRelationships()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var task = await AddEntityAsync(dataAccessLayer, ["task"], new EntityName("tasks", "withrel"));
        var user = await AddEntityAsync(dataAccessLayer, ["user"], new EntityName("users", "withrel"));
        var relationship = await AddRelationshipAsync(
            dataAccessLayer,
            ["assigned-to"],
            new EntityName("relationships", "withrel"),
            $$"""{ "target": "{{task.Value}}", "user": "{{user.Value}}" }""");

        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("by-type"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["task"]) },
                },
            ],
            RelationshipsToReturn = [new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]) }],
        });

        var taskEntity = Assert.Single(
            Assert.Single(result.Batches).Entities,
            entity => entity.EntityId == task);
        Assert.Contains(relationship, taskEntity.Relationships.Select(static relationshipSnapshot => relationshipSnapshot.EntityId));
    }

    protected static async Task<EntityId> AddEntityAsync(
        IDataAccessLayer dataAccessLayer,
        IReadOnlyList<string> entityTypes,
        EntityName name,
        string? text = null,
        string? status = null)
    {
        var guid = Guid.NewGuid();
        var entityId = new EntityId(guid);
        var entityTypesJson = JsonSerializer.Serialize(entityTypes);
        var namesJson = JsonSerializer.Serialize(new[] { name.Components });
        var contentJson = text is null
            ? string.Empty
            : $",\"content\":{{\"text\":{JsonSerializer.Serialize(text)}}}";
        var statusJson = status is null
            ? string.Empty
            : $",\"status\":{JsonSerializer.Serialize(status)}";

        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": {{entityTypesJson}},
              "names": {{namesJson}}{{contentJson}}{{statusJson}}
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

    protected static async Task<EntityId> AddRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        IReadOnlyList<string> relationshipTypes,
        EntityName name,
        string participantsJson)
    {
        var guid = Guid.NewGuid();
        var typesJson = JsonSerializer.Serialize(relationshipTypes.Append("relationship").ToArray());
        var namesJson = JsonSerializer.Serialize(new[] { name.Components });

        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": {{typesJson}},
              "names": {{namesJson}},
              "participants": {{participantsJson}}
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "add query test relationship" } },
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

    private static EntityParticipationQueryClause AssignedToForUser(EntityId user)
        => new()
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
            ParticipationRoleNames = new RoleNameSet(["target"]),
            MustHave = new EntityParticipationRequirement
            {
                ParticipationRoleNames = new RoleNameSet(["user"]),
                Clause = new EntityFieldQueryClause
                {
                    FieldPath = new FieldPath("entity-id"),
                    ComparisonOperator = FieldComparisonOperator.Equals,
                    Value = JsonSerializer.SerializeToElement(user.Value.ToString()),
                },
            },
        };

    private static QueryRequest BuildSingleClauseQuery(string clauseIdentifier, QueryClause clause) => new()
    {
        Clauses =
        [
            new TopLevelQueryClause
            {
                ClauseIdentifier = new QueryClauseIdentifier(clauseIdentifier),
                Clause = clause,
            },
        ],
    };

    protected static async Task<IReadOnlyList<QueryEntitySnapshot>> QueryAsync(
        IDataAccessLayer dataAccessLayer,
        string clauseIdentifier,
        QueryClause clause)
    {
        var result = await dataAccessLayer.QueryAsync(BuildSingleClauseQuery(clauseIdentifier, clause));
        var batch = Assert.Single(result.Batches);
        return batch.Entities.ToArray();
    }

    private static async Task<IReadOnlySet<EntityId>> QueryIdsAsync(IDataAccessLayer dataAccessLayer, QueryClause clause)
    {
        var matches = await QueryAsync(dataAccessLayer, "participation-contract", clause);
        return matches.Select(static entity => entity.EntityId).ToHashSet();
    }
}
