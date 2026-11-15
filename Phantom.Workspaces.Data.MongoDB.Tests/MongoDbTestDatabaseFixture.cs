using MongoDB.Driver;
using MongoDB.Bson;
using Microsoft.Extensions.Logging.Abstractions;
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

    private readonly string? isolatedConnection;
    private readonly Func<MongoDbConnectionDefinition, CancellationToken, ValueTask<IMongoClient>> connect;
    private readonly Func<string, IMongoClient> createOwnedClient;
    private IMongoClient? isolatedClient;
    private string? ownedDatabaseName;
    private bool isolatedMode;
    private MongoDbConnectionDefinition connectionDefinition;
    private bool initialized;

    public MongoDbTestDatabaseFixture()
        : this(Environment.GetEnvironmentVariable("PHANTOM_TEST_MONGODB_CONNECTION_STRING"))
    {
    }

    internal MongoDbTestDatabaseFixture(string? isolatedConnection,
        Func<MongoDbConnectionDefinition, CancellationToken, ValueTask<IMongoClient>>? connect = null,
        Func<string, IMongoClient>? createOwnedClient = null)
    {
        this.isolatedConnection = isolatedConnection;
        this.isolatedMode = isolatedConnection is not null;
        this.connect = connect ?? ((definition, token) => new MongoDbConnectionBroker(
            new WindowsDockerDesktopEngine(NullLogger<DockerCommandRunner>.Instance)).GetClientAsync(definition, token));
        this.createOwnedClient = createOwnedClient ?? (connection => new MongoClient(connection));
        this.connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = ContainerName,
            DataDirectory = DataDirectory,
            DatabaseName = DatabaseName,
            CollectionName = EntityCollectionName,
            HostPort = HostPort,
        };
    }

    public MongoDbConnectionDefinition ConnectionDefinition => this.initialized
        ? this.connectionDefinition
        : throw new InvalidOperationException("The MongoDB test fixture must be initialized before its connection is used.");

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (this.isolatedMode)
        {
            if (string.IsNullOrWhiteSpace(this.isolatedConnection))
                throw new InvalidOperationException("PHANTOM_TEST_MONGODB_CONNECTION_STRING must be a valid MongoDB connection string when configured.");
            _ = new MongoUrl(this.isolatedConnection);
            var databaseName = "pw_mongodb_test_" + Guid.NewGuid().ToString("N");
            this.connectionDefinition = MongoDbConnectionDefinition.CreateExternal(this.isolatedConnection, databaseName, EntityCollectionName);
            await this.InitializeOwnedClientAsync(this.createOwnedClient(this.isolatedConnection), databaseName,
                async database => { await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)); });
            this.initialized = true;
            return;
        }
        // The broker creates both bind-mount sources before starting the container.
        var client = await this.connect(this.connectionDefinition, CancellationToken.None);
        Database = client.GetDatabase(DatabaseName);

        await ResetCollectionAsync();
        this.initialized = true;
    }

    public async Task DisposeAsync()
    {
        this.initialized = false;
        if (this.isolatedMode)
        {
            if (this.isolatedClient is null) return;
            var client = this.isolatedClient;
            this.isolatedClient = null;
            await DropOwnedDatabaseAsync(client, this.ownedDatabaseName!);
            return;
        }
        // Leave the container running so subsequent runs reuse the warmed-up Atlas Local instance.
        // Only clean up collection state.
        await ResetCollectionAsync();
    }

    internal async Task InitializeOwnedClientAsync(IMongoClient client, string databaseName, Func<IMongoDatabase, Task> initialize)
    {
        this.isolatedMode = true;
        this.isolatedClient = client;
        this.ownedDatabaseName = databaseName;
        try
        {
            Database = client.GetDatabase(databaseName);
            await initialize(Database);
        }
        catch (Exception initializationFailure)
        {
            try { await this.DisposeAsync(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Private Mongo initialization and cleanup failed.", initializationFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal static async Task DropOwnedDatabaseAsync(IMongoClient client, string databaseName)
    {
        try { await client.DropDatabaseAsync(databaseName); }
        finally { ((IDisposable)client).Dispose(); }
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
        await TryDropCollectionAsync($"{ChatHistoryCollectionName}-sub-agent-manifests");
        await TryDropCollectionAsync($"{EntityCollectionName}_entities");
        await TryDropCollectionAsync($"{EntityCollectionName}_versions");
        await TryDropCollectionAsync($"{EntityCollectionName}_queue_heads");
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
