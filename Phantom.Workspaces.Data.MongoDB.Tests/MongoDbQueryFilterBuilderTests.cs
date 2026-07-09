using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>Unit tests for <see cref="MongoDbQueryFilterBuilder"/> — no Docker required.</summary>
public sealed class MongoDbQueryFilterBuilderTests
{
    private static BsonDocument Render(FilterDefinition<BsonDocument> filter)
        => filter.Render(new RenderArgs<BsonDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry));

    [Fact]
    public void Translate_AlwaysIncludesIsDeletedFilter()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new EntityTypeQueryClause
        {
            EntityTypeNames = new EntityTypeNameSet(["note"]),
        });

        var rendered = Render(filter);
        Assert.True(rendered.Contains("current.is-deleted"), "Every translated filter must include is-deleted guard");
    }

    [Fact]
    public void Translate_EntityType_UsesCurrentDataEntityTypes()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new EntityTypeQueryClause
        {
            EntityTypeNames = new EntityTypeNameSet(["note", "task"]),
        });

        var rendered = Render(filter);
        Assert.True(rendered["current.is-deleted"]["$ne"].AsBoolean);
        // Must use current.data.entity-types, NOT current.type-names
        Assert.True(rendered.Contains("current.data.entity-types"),
            "EntityTypeQueryClause must filter on current.data.entity-types (not current.type-names)");
        Assert.False(rendered.ToJson().Contains("type-names", StringComparison.Ordinal),
            "Filter must not reference old current.type-names field");
        Assert.Equal(2, rendered["current.data.entity-types"]["$all"].AsBsonArray.Count);
    }

    [Fact]
    public void Translate_And_ReturnsAndClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new AndQueryClause
        {
            Clauses =
            [
                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["task"]) },
            ],
        });

        var rendered = Render(filter);
        var json = rendered.ToJson();
        Assert.Contains("$and", json, StringComparison.Ordinal);
        Assert.Contains("current.data.entity-types", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_Or_ReturnsOrClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new OrQueryClause
        {
            Clauses =
            [
                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["task"]) },
            ],
        });

        var rendered = Render(filter);
        var json = rendered.ToJson();
        Assert.Contains("$or", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_Not_ReturnsNorClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new NotQueryClause
        {
            Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["archived"]) },
        });

        var rendered = Render(filter);
        var json = rendered.ToJson();
        // MongoDB driver renders Not as $nor
        Assert.Contains("$nor", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_Top_ReturnsInnerFilter()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new TopQueryClause
        {
            ResultLimit = new QueryResultLimit(5),
            Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
        });

        var rendered = Render(filter);
        var json = rendered.ToJson();
        Assert.Contains("current.data.entity-types", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_EntityField_Equals_ReturnsEqClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new EntityFieldQueryClause
        {
            FieldPath = new FieldPath("content", "text"),
            ComparisonOperator = FieldComparisonOperator.Equals,
            Value = JsonSerializer.SerializeToElement("hello"),
        });

        var rendered = Render(filter);
        Assert.Equal("hello", rendered["current.data.content.text"].AsString);
    }

    [Fact]
    public void Translate_EntityField_GreaterThan_ReturnsGtClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new EntityFieldQueryClause
        {
            FieldPath = new FieldPath("priority"),
            ComparisonOperator = FieldComparisonOperator.GreaterThan,
            Value = JsonSerializer.SerializeToElement(5),
        });

        var rendered = Render(filter);
        Assert.Equal(5, rendered["current.data.priority"]["$gt"].AsInt64);
    }

    [Fact]
    public void Translate_EntityField_Contains_ReturnsOrEqRegexClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new EntityFieldQueryClause
        {
            FieldPath = new FieldPath("title"),
            ComparisonOperator = FieldComparisonOperator.Contains,
            Value = JsonSerializer.SerializeToElement("hello"),
        });

        var rendered = Render(filter);
        var json = rendered.ToJson();
        // Contains translates to $or of Eq and Regex
        Assert.Contains("$or", json, StringComparison.Ordinal);
        Assert.Contains("current.data.title", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_EntityField_RegularExpressionMatch_ReturnsRegexClause()
    {
        var builder = new MongoDbQueryFilterBuilder();
        var filter = builder.TranslateToFilter(new EntityFieldQueryClause
        {
            FieldPath = new FieldPath("title"),
            ComparisonOperator = FieldComparisonOperator.RegularExpressionMatch,
            Value = JsonSerializer.SerializeToElement("^hello"),
        });

        var rendered = Render(filter);
        var json = rendered.ToJson();
        Assert.Contains("current.data.title", json, StringComparison.Ordinal);
        Assert.Contains("$regex", json, StringComparison.Ordinal);
    }
}
