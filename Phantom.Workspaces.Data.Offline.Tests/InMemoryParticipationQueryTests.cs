using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Offline.Tests;

/// <summary>
/// Tests the in-memory evaluation of <see cref="EntityParticipationQueryClause"/>, which selects
/// entities that participate (in given roles) in relationships of given types, optionally requiring
/// the relationship to also carry a participant matching a sub-clause (MustHave). This powers the
/// inbox/workstreams interest queries (e.g. tasks that are the target of an assigned-to interest
/// whose user participant is the current user).
/// </summary>
public sealed class InMemoryParticipationQueryTests
{
    [Fact]
    public async Task Participation_SelectsTargetsOfTypedRelationship_FilteredByMustHaveParticipant()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var user = await AddAsync(dataAccessLayer, """{ "entity-types": ["user"], "names": [["users","u"]] }""");
        var assignedTask = await AddAsync(dataAccessLayer, """{ "entity-types": ["task"], "names": [["tasks","assigned"]] }""");
        var unassignedTask = await AddAsync(dataAccessLayer, """{ "entity-types": ["task"], "names": [["tasks","unassigned"]] }""");

        // An assigned-to interest linking the assigned task (target) to the user.
        await AddAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["assigned-to","relationship"],
              "names": [["relationships","r1"]],
              "participants": { "target": "{{assignedTask.Value}}", "user": "{{user.Value}}" }
            }
            """);

        // A different relationship type that should be excluded by the type filter.
        await AddAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["related","relationship"],
              "names": [["relationships","r2"]],
              "participants": { "entities": ["{{unassignedTask.Value}}", "{{user.Value}}"] }
            }
            """);

        var matches = await QueryAsync(dataAccessLayer, new EntityParticipationQueryClause
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
            ParticipationRoleNames = new RoleNameSet(["target"]),
            MustHave = new EntityParticipationRequirement
            {
                ParticipationRoleNames = new RoleNameSet(["user"]),
                Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["user"]) },
            },
        });

        Assert.Contains(assignedTask, matches);
        Assert.DoesNotContain(unassignedTask, matches);
        // The user participates as 'user', not 'target', so it is not returned.
        Assert.DoesNotContain(user, matches);
    }

    [Fact]
    public async Task Participation_WithoutMustHave_ReturnsRequestedRoleParticipants()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var user = await AddAsync(dataAccessLayer, """{ "entity-types": ["user"], "names": [["users","u"]] }""");
        var task = await AddAsync(dataAccessLayer, """{ "entity-types": ["task"], "names": [["tasks","t"]] }""");
        await AddAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["assigned-to","relationship"],
              "names": [["relationships","r"]],
              "participants": { "target": "{{task.Value}}", "user": "{{user.Value}}" }
            }
            """);

        var targets = await QueryAsync(dataAccessLayer, new EntityParticipationQueryClause
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
            ParticipationRoleNames = new RoleNameSet(["target"]),
        });
        Assert.Equal(new[] { task }, targets.ToArray());

        var users = await QueryAsync(dataAccessLayer, new EntityParticipationQueryClause
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
            ParticipationRoleNames = new RoleNameSet(["user"]),
        });
        Assert.Equal(new[] { user }, users.ToArray());
    }

    private static async Task<IReadOnlyCollection<EntityId>> QueryAsync(
        IDataAccessLayer dataAccessLayer,
        EntityParticipationQueryClause clause)
    {
        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("participation"),
                    Clause = clause,
                },
            ],
        });
        return result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.EntityId).ToArray();
    }

    private static async Task<EntityId> AddAsync(IDataAccessLayer dataAccessLayer, string json)
    {
        var guid = Guid.NewGuid();
        using var template = JsonDocument.Parse(json);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", guid);
            foreach (var property in template.RootElement.EnumerateObject())
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
                    EntityId = new EntityId(guid),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        Assert.DoesNotContain(result.EntityResults, static r => r.UpdateState == UpdateState.Failed);
        return new EntityId(guid);
    }
}
