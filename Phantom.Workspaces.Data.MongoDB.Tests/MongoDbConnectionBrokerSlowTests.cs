using MongoDB.Bson;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbConnectionBrokerSlowTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbConnectionBrokerSlowTests(
        MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetClientAsync_WhenContainerConfigured_ReturnsConnectedClient()
    {
        await _fixture.ResetCollectionAsync();

        var broker = new MongoDbConnectionBroker();
        var client = await broker.GetClientAsync(_fixture.ConnectionDefinition);
        var database = client.GetDatabase(_fixture.ConnectionDefinition.DatabaseName);
        var collection = database.GetCollection<BsonDocument>(_fixture.ConnectionDefinition.CollectionName);

        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", "connected-client" },
            { "value", 1 },
        });

        var count = await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NonQuery_InsertUpdateDelete_WithStaticCollection_ResetsStateAtTestStart()
    {
        await _fixture.ResetCollectionAsync();

        var collection = _fixture.Database.GetCollection<BsonDocument>(MongoDbTestDatabaseFixture.EntityCollectionName);
        var documentId = "non-query-document";

        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", documentId },
            { "value", "initial" },
        });

        var replaceResult = await collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", documentId),
            new BsonDocument
            {
                { "_id", documentId },
                { "value", "updated" },
            });

        var deleteResult = await collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", documentId));
        var count = await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);

        Assert.Equal(1, replaceResult.ModifiedCount);
        Assert.Equal(1, deleteResult.DeletedCount);
        Assert.Equal(0, count);
    }
}
