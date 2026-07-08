using MongoDB.Bson;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>Unit tests for <see cref="MongoDbEntityDataAccessLayer.BuildGetFilterDocument"/> — no Docker required.</summary>
public sealed class MongoDbGetAsyncFilterTests
{
    private static readonly EntityId EntityA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly EntityId EntityB = new("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void BuildGetFilterDocument_EmptyEntities_ProducesEmptyDocument()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument([]);

        Assert.Empty(filter);
    }

    [Fact]
    public void BuildGetFilterDocument_ById_ProducesIdEquality()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest { EntityId = EntityA },
        ]);

        Assert.True(filter.Contains("_id"), "Filter must target _id");
        Assert.Equal(EntityA.ToString(), filter["_id"].AsString);
    }

    [Fact]
    public void BuildGetFilterDocument_ByEntityType_ProducesTypeInFilter()
    {
        var typeNames = new EntityTypeNameSet(["note", "task"]);
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest { EntityTypeNames = typeNames },
        ]);

        Assert.True(filter.Contains("current.type-names"), "Filter must target current.type-names");
        var inValues = filter["current.type-names"]["$in"].AsBsonArray
            .Select(static v => v.AsString)
            .ToArray();
        Assert.Contains("note", inValues, StringComparer.Ordinal);
        Assert.Contains("task", inValues, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildGetFilterDocument_ByEntityNameSelf_ProducesNamesArrayFilter()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest
            {
                EntityName = new EntityName("workspace", "dev"),
                EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
            },
        ]);

        Assert.True(filter.Contains("current.names"), "Filter must target current.names");
        var components = filter["current.names"].AsBsonArray.Select(static v => v.AsString).ToArray();
        Assert.Equal(["workspace", "dev"], components);
    }

    [Fact]
    public void BuildGetFilterDocument_ByEntityNameSelf_SingleComponent_ProducesNamesArrayFilter()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest
            {
                EntityName = new EntityName("my-entity"),
                EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
            },
        ]);

        Assert.True(filter.Contains("current.names"), "Filter must target current.names");
        var components = filter["current.names"].AsBsonArray.Select(static v => v.AsString).ToArray();
        Assert.Equal(["my-entity"], components);
    }

    [Fact]
    public void BuildGetFilterDocument_ByTypeAndNameSelf_ProducesAndFilter()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest
            {
                EntityTypeNames = new EntityTypeNameSet(["note"]),
                EntityName = new EntityName("my-note"),
                EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
            },
        ]);

        Assert.True(filter.Contains("$and"), "Filter must use $and to combine type and name");
        var andClauses = filter["$and"].AsBsonArray.Select(static v => v.AsBsonDocument).ToArray();
        Assert.Contains(andClauses, c => c.Contains("current.type-names"));
        Assert.Contains(andClauses, c => c.Contains("current.names"));
    }

    [Fact]
    public void BuildGetFilterDocument_ByEntityIdAndType_ProducesOrFilter()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest { EntityId = EntityA },
            new GetEntityRequest { EntityTypeNames = new EntityTypeNameSet(["task"]) },
        ]);

        Assert.True(filter.Contains("$or"), "Filter must use $or for multiple sub-requests");
        var orClauses = filter["$or"].AsBsonArray.Select(static v => v.AsBsonDocument).ToArray();
        Assert.Equal(2, orClauses.Length);
        Assert.Contains(orClauses, c => c.Contains("_id"));
        Assert.Contains(orClauses, c => c.Contains("current.type-names"));
    }

    [Fact]
    public void BuildGetFilterDocument_MultipleEntityIds_ProducesOrFilter()
    {
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest { EntityId = EntityA },
            new GetEntityRequest { EntityId = EntityB },
        ]);

        Assert.True(filter.Contains("$or"), "Multiple id sub-requests must use $or");
        var ids = filter["$or"].AsBsonArray
            .Select(static v => v.AsBsonDocument["_id"].AsString)
            .ToArray();
        Assert.Contains(EntityA.ToString(), ids, StringComparer.Ordinal);
        Assert.Contains(EntityB.ToString(), ids, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildGetFilterDocument_ByEntityNameChildren_WithNoTypeFilter_ProducesEmptyDocument()
    {
        // EnumerateChildren with no type filter cannot be expressed as a targeted filter.
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest
            {
                EntityName = new EntityName("workspace"),
                EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
            },
        ]);

        Assert.Empty(filter);
    }

    [Fact]
    public void BuildGetFilterDocument_ByEntityNameChildren_WithTypeFilter_ProducesTypeFilter()
    {
        // EnumerateChildren with a type filter: pre-filter by type, post-filter handles prefix match.
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest
            {
                EntityTypeNames = new EntityTypeNameSet(["folder"]),
                EntityName = new EntityName("workspace"),
                EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
            },
        ]);

        Assert.True(filter.Contains("current.type-names"), "Filter must target current.type-names");
    }

    [Fact]
    public void BuildGetFilterDocument_MixedTargetedAndNonTargeted_ProducesEmptyDocument()
    {
        // When any sub-request requires full scan, the whole filter falls back to full scan.
        var filter = MongoDbEntityDataAccessLayer.BuildGetFilterDocument(
        [
            new GetEntityRequest { EntityId = EntityA },
            new GetEntityRequest
            {
                EntityName = new EntityName("workspace"),
                EnumerateChildren = EnumerateChildrenAction.EnumerateAllChildren,
            },
        ]);

        Assert.Empty(filter);
    }
}
