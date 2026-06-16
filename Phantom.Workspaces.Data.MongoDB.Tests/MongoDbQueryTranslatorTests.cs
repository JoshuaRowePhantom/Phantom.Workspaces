using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;
using Xunit;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbQueryTranslatorTests
{
    private static BsonDocument Render(FilterDefinition<BsonDocument> filter)
        => filter.Render(new RenderArgs<BsonDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry));

    [Fact]
    public void EntityType_TranslatesToAllOnProjectedTypeNames_ExcludingDeleted()
    {
        var translator = new MongoDbQueryTranslator();

        var filter = translator.TranslateToFilter(new EntityTypeQueryClause
        {
            EntityTypeNames = new EntityTypeNameSet(["note", "task"]),
        });

        var rendered = Render(filter);
        // The driver flattens the not-deleted guard and the type constraint into one document.
        Assert.Equal(true, rendered["current.is-deleted"]["$ne"].AsBoolean);
        Assert.Equal(2, rendered["current.type-names"]["$all"].AsBsonArray.Count);
    }

    [Fact]
    public void And_Or_Not_Compose()
    {
        var translator = new MongoDbQueryTranslator();

        var filter = translator.TranslateToFilter(new AndQueryClause
        {
            Clauses =
            [
                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
                new OrQueryClause
                {
                    Clauses =
                    [
                        new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["important"]) },
                        new NotQueryClause
                        {
                            Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["archived"]) },
                        },
                    ],
                },
            ],
        });

        var rendered = Render(filter);
        // The rendered filter is a valid composed document; assert the operators and field paths
        // survive composition (the driver may flatten nested $and, so do not require it).
        var json = rendered.ToJson();
        Assert.Contains("$or", json, System.StringComparison.Ordinal);
        Assert.Contains("current.type-names", json, System.StringComparison.Ordinal);
        Assert.Contains("current.is-deleted", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GetResultLimit_ReturnsTopLimit()
    {
        var clause = new TopQueryClause
        {
            ResultLimit = new QueryResultLimit(5),
            Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) },
        };

        Assert.Equal(5, MongoDbQueryTranslator.GetResultLimit(clause));
        Assert.Null(MongoDbQueryTranslator.GetResultLimit(
            new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["note"]) }));
    }

    [Fact]
    public void VectorClause_AsFilter_Throws()
    {
        var translator = new MongoDbQueryTranslator();
        var clause = new EntityVectorQueryClause
        {
            VectorQueryIdentifier = new QueryClauseIdentifier("v"),
            QueryEmbedding = [0.1f, 0.2f],
        };

        Assert.Throws<System.NotSupportedException>(() => translator.TranslateToFilter(clause));
    }

    [Fact]
    public void BuildVectorSearchStage_EmitsParameterizedVectorSearch()
    {
        var clause = new EntityVectorQueryClause
        {
            VectorQueryIdentifier = new QueryClauseIdentifier("v"),
            QueryEmbedding = [0.5f, -0.25f, 0.75f],
            NumberOfCandidates = 4,
        };

        var stage = MongoDbQueryTranslator.BuildVectorSearchStage(clause, "entity-vector-index");

        var vectorSearch = stage["$vectorSearch"].AsBsonDocument;
        Assert.Equal("entity-vector-index", vectorSearch["index"].AsString);
        Assert.Equal("current.embedding", vectorSearch["path"].AsString);
        Assert.Equal(3, vectorSearch["queryVector"].AsBsonArray.Count);
        Assert.Equal(4, vectorSearch["limit"].AsInt32);
        Assert.Equal(40, vectorSearch["numCandidates"].AsInt32);
        Assert.False(vectorSearch.Contains("filter"));
    }

    [Fact]
    public void BuildVectorSearchStage_WithPreFilter_IncludesRenderedFilter()
    {
        var clause = new EntityVectorQueryClause
        {
            VectorQueryIdentifier = new QueryClauseIdentifier("v"),
            QueryEmbedding = [1f, 0f],
        };
        var preFilter = Builders<BsonDocument>.Filter.All("current.type-names", new[] { "note" });

        var stage = MongoDbQueryTranslator.BuildVectorSearchStage(clause, "idx", preFilter);

        var vectorSearch = stage["$vectorSearch"].AsBsonDocument;
        Assert.True(vectorSearch.Contains("filter"));
        Assert.True(vectorSearch["filter"].AsBsonDocument.Contains("current.type-names"));
    }

    [Fact]
    public void BuildVectorSearchStage_WithoutEmbedding_Throws()
    {
        var clause = new EntityVectorQueryClause
        {
            VectorQueryIdentifier = new QueryClauseIdentifier("v"),
            QueryText = "needs embedding",
        };

        Assert.Throws<System.ArgumentException>(() => MongoDbQueryTranslator.BuildVectorSearchStage(clause, "idx"));
    }
}
