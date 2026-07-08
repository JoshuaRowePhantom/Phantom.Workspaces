using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class NotInterestingQueryTests
{
    [PhantomAvaloniaFact]
    public async Task ExcludingNotInteresting_RemovesNotInterestingTargetsFromQueryResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var userId = new EntityId("d1d1d1d1-0000-0000-0000-000000000001");
        var visibleId = new EntityId("d2d2d2d2-0000-0000-0000-000000000002");
        var hiddenId = new EntityId("d3d3d3d3-0000-0000-0000-000000000003");
        await SeedAsync(dataAccessLayer, userId, """{ "entity-types": ["entity", "user"], "names": [["users","u","u"]] }""");
        await SeedAsync(dataAccessLayer, visibleId, """{ "entity-types": ["entity", "task"], "names": [["tasks","visible"]] }""");
        await SeedAsync(dataAccessLayer, hiddenId, """{ "entity-types": ["entity", "task"], "names": [["tasks","hidden"]] }""");
        await SeedAsync(dataAccessLayer, new EntityId(Guid.NewGuid()), $$"""{ "entity-types": ["entity", "actionable","relationship"], "participants": { "target": "{{visibleId.Value}}", "user": "{{userId.Value}}" } }""");
        await SeedAsync(dataAccessLayer, new EntityId(Guid.NewGuid()), $$"""{ "entity-types": ["entity", "actionable","relationship"], "participants": { "target": "{{hiddenId.Value}}", "user": "{{userId.Value}}" } }""");
        await SeedAsync(dataAccessLayer, new EntityId(Guid.NewGuid()), $$"""{ "entity-types": ["entity", "not-interesting","relationship"], "participants": { "target": "{{hiddenId.Value}}" } }""");

        var inboxQuery = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("actionable"),
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(["actionable"]),
                        ParticipationRoleNames = new RoleNameSet(["target"]),
                        MustHave = new EntityParticipationRequirement
                        {
                            ParticipationRoleNames = new RoleNameSet(["user"]),
                            Clause = new EntityFieldQueryClause
                            {
                                FieldPath = new FieldPath("entity-id"),
                                ComparisonOperator = FieldComparisonOperator.Equals,
                                Value = JsonSerializer.SerializeToElement(userId.Value.ToString()),
                            },
                        },
                    },
                },
            ],
        };

        var unfilteredQuery = await broker.SubscribeQueryAsync(inboxQuery, ct);
        var unfiltered = unfilteredQuery.Results.Select(entity => entity.EntityId).ToHashSet();
        Assert.Contains(visibleId, unfiltered);
        Assert.Contains(hiddenId, unfiltered);

        var filteredQuery = await broker.SubscribeQueryAsync(NotInterestingQuery.ExcludingNotInteresting(inboxQuery), ct);
        var filtered = filteredQuery.Results.Select(entity => entity.EntityId).ToHashSet();
        Assert.Contains(visibleId, filtered);
        Assert.DoesNotContain(hiddenId, filtered);
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
        }, CancellationToken.None);

        var failure = result.EntityResults.FirstOrDefault(static entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.True(
            failure is null,
            failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static error => error.Message)));
    }
}
