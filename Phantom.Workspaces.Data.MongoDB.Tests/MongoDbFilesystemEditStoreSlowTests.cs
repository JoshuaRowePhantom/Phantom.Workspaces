using MongoDB.Driver;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbFilesystemEditStoreSlowTests : FilesystemEditStoreContractTests
{
    private readonly MongoDbTestDatabaseFixture fixture;

    public MongoDbFilesystemEditStoreSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        this.fixture = fixture;
    }

    protected override ValueTask<IFilesystemEditStore> CreateStoreAsync()
    {
        var collection = this.fixture.Database.GetCollection<MongoDbFilesystemEditDocument>(
            MongoDbTestDatabaseFixture.FilesystemEditCollectionName);
        return ValueTask.FromResult<IFilesystemEditStore>(new MongoDbFilesystemEditStore(collection));
    }

    protected override async ValueTask ResetStoreAsync()
    {
        await this.fixture.ResetCollectionAsync();
    }

    [Fact]
    public async Task GetEditAsync_WhenIdIsNotObjectId_ReturnsNull()
    {
        var collection = this.fixture.Database.GetCollection<MongoDbFilesystemEditDocument>(
            MongoDbTestDatabaseFixture.FilesystemEditCollectionName);
        var store = new MongoDbFilesystemEditStore(collection);

        var stored = await store.GetEditAsync("not-an-object-id", CancellationToken.None);

        Assert.Null(stored);
    }
}
