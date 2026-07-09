using MongoDB.Bson;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>Unit tests for <see cref="MongoDbGetFilterBuilder"/> — no Docker required.</summary>
public sealed class MongoDbGetFilterBuilderTests
{
    private static readonly EntityId EntityA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly EntityId EntityB = new("bbbbbbbb-0000-0000-0000-000000000002");

    // ─── is-deleted guard ─────────────────────────────────────────────────────

    [Fact]
    public void BuildEntityFilter_AlwaysIncludesIsDeletedFilter()
    {
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(new GetEntityRequest { EntityId = EntityA });
        var json = filter.ToJson();
        Assert.Contains("current.is-deleted", json, StringComparison.Ordinal);
    }

    // ─── By ID ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildEntityFilter_ById_ReturnsIdClause()
    {
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(new GetEntityRequest { EntityId = EntityA });

        Assert.True(filter.Contains("_id"), "Filter must target _id");
        Assert.Equal(EntityA.ToString(), filter["_id"].AsString);
        Assert.True(filter.Contains("current.is-deleted"), "Filter must include is-deleted guard");
    }

    // ─── By entity type ───────────────────────────────────────────────────────

    [Fact]
    public void BuildEntityFilter_ByEntityType_Single_ReturnsMultikeyEqualityClause()
    {
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(
            new GetEntityRequest { EntityTypeNames = new EntityTypeNameSet(["user"]) });

        Assert.True(filter.Contains("current.data.entity-types"), "Filter must target current.data.entity-types");
        // Single type → scalar equality (MongoDB multikey: matches docs where "user" is in the array)
        Assert.Equal("user", filter["current.data.entity-types"].AsString);
    }

    [Fact]
    public void BuildEntityFilter_ByEntityType_Multiple_ReturnsInClause()
    {
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(
            new GetEntityRequest { EntityTypeNames = new EntityTypeNameSet(["user", "tool"]) });

        Assert.True(filter.Contains("current.data.entity-types"), "Filter must target current.data.entity-types");
        var inValues = filter["current.data.entity-types"]["$in"].AsBsonArray
            .Select(static v => v.AsString)
            .ToArray();
        Assert.Contains("user", inValues, StringComparer.Ordinal);
        Assert.Contains("tool", inValues, StringComparer.Ordinal);
    }

    // ─── By entity name ───────────────────────────────────────────────────────

    [Fact]
    public void BuildEntityFilter_ByEntityName_EnumerateSelf_ReturnsArrayElementMatchClause()
    {
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(new GetEntityRequest
        {
            EntityName = new EntityName("users", "jrowe"),
            EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
        });

        Assert.True(filter.Contains("current.data.names"), "Filter must target current.data.names");
        var components = filter["current.data.names"].AsBsonArray.Select(static v => v.AsString).ToArray();
        Assert.Equal(["users", "jrowe"], components);
    }

    [Fact]
    public void BuildEntityFilter_ByEntityName_EnumerateChildren_UsesNameParentPrefixesAndExprDepthCheck()
    {
        // EnumerateChildren: direct children of ["computers","hostname"]
        // Expected: { "current.name-parent-prefixes": ["computers","hostname"], $expr: {...} }
        var prefix = new EntityName("computers", "hostname");
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(new GetEntityRequest
        {
            EntityName = prefix,
            EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
        });

        var json = filter.ToJson();
        Assert.Contains("current.name-parent-prefixes", json, StringComparison.Ordinal);
        Assert.Contains("$expr", json, StringComparison.Ordinal);
        Assert.Contains("current.data.names", json, StringComparison.Ordinal);
        // Depth check: prefix.length + 1 = 3
        Assert.Contains("\"3\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEntityFilter_ByEntityName_EnumerateAllChildren_UsesNameParentPrefixesOnly()
    {
        var filter = MongoDbGetFilterBuilder.BuildEntityFilter(new GetEntityRequest
        {
            EntityName = new EntityName("computers", "hostname"),
            EnumerateChildren = EnumerateChildrenAction.EnumerateAllChildren,
        });

        Assert.True(filter.Contains("current.name-parent-prefixes"), "Filter must target current.name-parent-prefixes");
        var components = filter["current.name-parent-prefixes"].AsBsonArray.Select(static v => v.AsString).ToArray();
        Assert.Equal(["computers", "hostname"], components);
        // No $expr for all-children — depth is not constrained
        Assert.False(filter.Contains("$expr"), "EnumerateAllChildren must not include $expr depth check");
    }

    // ─── Relationship loading filter ──────────────────────────────────────────

    [Fact]
    public void BuildRelationshipFilter_ByParticipantIds_ReturnsInClause()
    {
        var entityIds = new[] { EntityA.ToString(), EntityB.ToString() };
        var filter = MongoDbGetFilterBuilder.BuildRelationshipFilter(entityIds);

        Assert.True(filter.Contains("current.participant-ids"), "Filter must target current.participant-ids");
        var inValues = filter["current.participant-ids"]["$in"].AsBsonArray
            .Select(static v => v.AsString)
            .ToArray();
        Assert.Contains(EntityA.ToString(), inValues, StringComparer.Ordinal);
        Assert.Contains(EntityB.ToString(), inValues, StringComparer.Ordinal);
        Assert.True(filter.Contains("current.is-deleted"), "Relationship filter must include is-deleted guard");
    }
}
