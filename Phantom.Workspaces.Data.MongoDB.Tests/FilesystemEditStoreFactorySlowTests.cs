using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoTestDatabaseCollection.CollectionName)]
public sealed class FilesystemEditStoreFactorySlowTests
{
    private readonly MongoTestDatabaseFixture fixture;

    public FilesystemEditStoreFactorySlowTests(MongoTestDatabaseFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task CreateAsync_WithMongoDbConnection_ReturnsMongoDbFilesystemEditStore()
    {
        await this.fixture.ResetCollectionAsync();
        var connectionDefinition = ChatHistoryProviderDefinition.CreateMongoDb(
            provider: "container",
            databaseName: MongoTestDatabaseFixture.DatabaseName,
            collectionName: MongoTestDatabaseFixture.FilesystemEditCollectionName,
            containerName: this.fixture.ConnectionDefinition.ContainerName,
            dataDirectory: this.fixture.ConnectionDefinition.DataDirectory,
            hostPort: MongoTestDatabaseFixture.HostPort);

        var store = await FilesystemEditStoreFactory.CreateAsync(connectionDefinition.ToJson(), CancellationToken.None);

        Assert.IsType<MongoDbFilesystemEditStore>(store);
    }
}
