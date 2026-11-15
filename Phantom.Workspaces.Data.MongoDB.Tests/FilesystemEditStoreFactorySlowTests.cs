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
        var connectionDefinition = this.fixture.ConnectionDefinition switch
        {
            MongoDbExternalConnectionDefinition external => ChatHistoryProviderDefinition.CreateMongoDb(
                provider: "external", databaseName: external.DatabaseName,
                collectionName: MongoDbTestDatabaseFixture.FilesystemEditCollectionName,
                connectionString: external.ConnectionString),
            MongoDbContainerConnectionDefinition container => ChatHistoryProviderDefinition.CreateMongoDb(
                provider: "container",
                databaseName: container.DatabaseName,
                collectionName: MongoDbTestDatabaseFixture.FilesystemEditCollectionName,
                containerName: container.ContainerName,
                dataDirectory: container.DataDirectory,
                hostPort: container.HostPort),
            _ => throw new InvalidOperationException("Unsupported fixture connection."),
        };

        var store = await FilesystemEditStoreFactory.CreateAsync(connectionDefinition.ToJson(),
            this.fixture.ConnectionDefinition is MongoDbExternalConnectionDefinition
                ? (_, _) => ValueTask.FromResult(this.fixture.Database.Client)
                : null, CancellationToken.None);

        Assert.IsType<MongoDbFilesystemEditStore>(store);
    }
}
