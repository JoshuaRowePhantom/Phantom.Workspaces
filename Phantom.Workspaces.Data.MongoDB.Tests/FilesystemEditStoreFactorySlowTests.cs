using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class FilesystemEditStoreFactorySlowTests
{
    private readonly MongoDbTestDatabaseFixture fixture;

    public FilesystemEditStoreFactorySlowTests(MongoDbTestDatabaseFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task CreateAsync_WithMongoDbConnection_ReturnsMongoDbFilesystemEditStore()
    {
        await this.fixture.ResetCollectionAsync();
        var connectionDefinition = ChatHistoryProviderDefinition.CreateMongoDb(
            provider: "container",
            databaseName: MongoDbTestDatabaseFixture.DatabaseName,
            collectionName: MongoDbTestDatabaseFixture.FilesystemEditCollectionName,
            containerName: this.fixture.ConnectionDefinition.ContainerName,
            dataDirectory: this.fixture.ConnectionDefinition.DataDirectory,
            hostPort: MongoDbTestDatabaseFixture.HostPort);

        var store = await FilesystemEditStoreFactory.CreateAsync(connectionDefinition.ToJson(), CancellationToken.None);

        Assert.IsType<MongoDbFilesystemEditStore>(store);
    }
}
