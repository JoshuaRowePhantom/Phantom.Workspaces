using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Offline.Tests;

/// <summary>
/// Verifies the query clause used by <c>sessions-view.json</c> to keep sub-agent sessions out of
/// the top-level sessions list: agent-session entities with a non-empty
/// <c>parent-agent-session-ids</c> array are excluded, while parentless (dispatcher and ordinary)
/// sessions are included.
/// </summary>
public sealed class SessionsViewParentFilterTests
{
    [Fact]
    public async Task TopLevelAgentSessionsQuery_ExcludesSessionsWithParent()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        var topLevel = await AddAsync(
            dataAccessLayer,
            """{ "entity-types": ["entity", "agent-session"], "names": [["sessions","top"]], "agent-session-id": "top" }""");

        var dispatcher = await AddAsync(
            dataAccessLayer,
            """{ "entity-types": ["entity", "agent-session"], "names": [["dispatchers","d"]], "agent-session-id": "d" }""");

        var subAgent = await AddAsync(
            dataAccessLayer,
            $$"""
            {
              "entity-types": ["entity", "agent-session"],
              "names": [["dispatchers","d","child"]],
              "agent-session-id": "child",
              "sub-agent-description": "a sub agent",
              "parent-agent-session-ids": ["{{dispatcher.Value}}"]
            }
            """);

        var matches = await QueryAsync(dataAccessLayer);

        Assert.Contains(topLevel, matches);
        Assert.Contains(dispatcher, matches);
        Assert.DoesNotContain(subAgent, matches);
    }

    private static async Task<IReadOnlyCollection<EntityId>> QueryAsync(IDataAccessLayer dataAccessLayer)
    {
        var clause = new AndQueryClause
        {
            Clauses =
            [
                new EntityTypeQueryClause
                {
                    EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                },
                new NotQueryClause
                {
                    Clause = new EntityFieldQueryClause
                    {
                        FieldPath = new FieldPath("parent-agent-session-ids", "0"),
                        ComparisonOperator = FieldComparisonOperator.RegularExpressionMatch,
                        Value = JsonSerializer.SerializeToElement(".*"),
                    },
                },
            ],
        };

        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("top-level-agent-sessions"),
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
