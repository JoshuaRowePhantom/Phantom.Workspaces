using MongoDB.Driver;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[CollectionDefinition(CollectionName)]
public sealed class MongoTestDatabaseCollection : ICollectionFixture<MongoTestDatabaseFixture>
{
    public const string CollectionName = "MongoTestDatabase";
}

public sealed class MongoTestDatabaseFixture : IAsyncLifetime
{
    public const string ContainerName = "pw-mongodb-test";
    public const string DatabaseName = "pw_mongodb_test_db";
    public const string CollectionName = "pw_mongodb_test_collection";
    public const string ChatHistoryCollectionName = "pw_mongodb_test_chat_history";
    public const int HostPort = 37017;

    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "pw-mongodb-test-data");

    private readonly ContainerEngine _containerEngine = new WindowsDockerDesktopEngine();
    private readonly MongoConnectionBroker _connectionBroker;

    public MongoTestDatabaseFixture()
    {
        ConnectionDefinition = new MongoDBContainerConnectionDefinition
        {
            ContainerName = ContainerName,
            DataDirectory = DataDirectory,
            DatabaseName = DatabaseName,
            CollectionName = CollectionName,
            HostPort = HostPort,
        };
        _connectionBroker = new MongoConnectionBroker(_containerEngine);
    }

    public MongoDBContainerConnectionDefinition ConnectionDefinition { get; }

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        await TryDestroyAsync();

        var client = await _connectionBroker.GetClientAsync(ConnectionDefinition);
        Database = client.GetDatabase(DatabaseName);

        await ResetCollectionAsync();
    }

    public async Task DisposeAsync()
    {
        await ResetCollectionAsync();
        await TryDestroyAsync();
        TryDeleteDirectory(DataDirectory);
    }

    public async Task ResetCollectionAsync()
    {
        if (Database is null)
        {
            return;
        }

        await TryDropCollectionAsync(CollectionName);
        await TryDropCollectionAsync(ChatHistoryCollectionName);
        await TryDropCollectionAsync($"{CollectionName}_entities");
        await TryDropCollectionAsync($"{CollectionName}_state");
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

    private async Task TryDestroyAsync()
    {
        try
        {
            await _containerEngine.DestroyAsync(ContainerName);
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup for deterministic reruns.
        }
    }

    private static void TryDeleteDirectory(
        string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for deterministic reruns.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for deterministic reruns.
        }
    }
}
