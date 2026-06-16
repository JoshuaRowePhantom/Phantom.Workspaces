using MongoDB.Driver;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[CollectionDefinition(CollectionName)]
public sealed class MongoDbTestDatabaseCollection : ICollectionFixture<MongoDbTestDatabaseFixture>
{
    public const string CollectionName = "MongoTestDatabase";
}

public sealed class MongoDbTestDatabaseFixture : IAsyncLifetime
{
    public const string ContainerName = "pw-mongodb-test";
    public const string DatabaseName = "pw_mongodb_test_db";
    public const string EntityCollectionName = "pw_mongodb_test_collection";
    public const string ChatHistoryCollectionName = "pw_mongodb_test_chat_history_messages";
    public const string FilesystemEditCollectionName = "pw_mongodb_test_filesystem_edits";
    public const int HostPort = 37017;

    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "pw-mongodb-test-data");

    private readonly ContainerEngine _containerEngine = new WindowsDockerDesktopEngine();
    private readonly MongoDbConnectionBroker _connectionBroker;

    public MongoDbTestDatabaseFixture()
    {
        ConnectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = ContainerName,
            DataDirectory = DataDirectory,
            DatabaseName = DatabaseName,
            CollectionName = EntityCollectionName,
            HostPort = HostPort,
        };
        _connectionBroker = new MongoDbConnectionBroker(_containerEngine);
    }

    public MongoDbContainerConnectionDefinition ConnectionDefinition { get; }

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);

        // Reuse a long-lived container across test runs. The broker starts the existing container or
        // creates one if absent. Destroying/recreating per run is expensive (Atlas Local performs a
        // one-time replica-set + search-service initialization that restarts mongod and can take
        // tens of seconds), so we leave it running and only reset collection state.
        var client = await _connectionBroker.GetClientAsync(ConnectionDefinition);
        Database = client.GetDatabase(DatabaseName);

        await ResetCollectionAsync();
    }

    public async Task DisposeAsync()
    {
        // Leave the container running so subsequent runs reuse the warmed-up Atlas Local instance.
        // Only clean up collection state.
        await ResetCollectionAsync();
    }

    public async Task ResetCollectionAsync()
    {
        if (Database is null)
        {
            return;
        }

        await TryDropCollectionAsync(EntityCollectionName);
        await TryDropCollectionAsync(ChatHistoryCollectionName);
        await TryDropCollectionAsync($"{ChatHistoryCollectionName}-agents");
        await TryDropCollectionAsync($"{ChatHistoryCollectionName}-messages");
        await TryDropCollectionAsync($"{EntityCollectionName}_entities");
        await TryDropCollectionAsync(FilesystemEditCollectionName);
    }

    private async Task TryDropCollectionAsync(string name)
    {
        try
        {
            await Database.DropCollectionAsync(name);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "NamespaceNotFound")
        {
            // Collection does not exist yet.
        }
    }
}
