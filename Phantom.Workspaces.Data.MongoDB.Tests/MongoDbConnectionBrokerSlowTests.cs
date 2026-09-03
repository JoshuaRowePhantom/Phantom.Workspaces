using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Containers;

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

    // #1415: a machine whose persisted /data/db was pinned to a previous (ephemeral) container
    // hostname comes up as RSGhost / no-primary. The broker must force-reconfigure the single-node
    // set onto the stable host so a writable primary is elected.
    [Fact]
    public async Task EnsureContainerStarted_MismatchedPersistedReplicaSet_ReconfiguresToPrimary()
    {
        var identifier = $"pw-mongo-1415-mismatch-{Guid.NewGuid():N}".Substring(0, 40);
        var dataDirectory = Path.Combine(Path.GetTempPath(), identifier);
        var connection = new MongoDbContainerConnectionDefinition
        {
            ContainerName = identifier,
            DataDirectory = dataDirectory,
            DatabaseName = "pw_1415_db",
            CollectionName = "pw_1415_collection",
            HostPort = 37037,
        };
        var staleHostname = "old-ephemeral-container-id";
        var engine = new WindowsDockerDesktopEngine(NullLogger<DockerCommandRunner>.Instance);

        try
        {
            Directory.CreateDirectory(dataDirectory);

            // Phase A: bring the node up with a STALE hostname so Atlas Local commits a persisted
            // replica-set config whose only member is the stale host, then remove the container while
            // /data/db (the config) persists.
            await StartContainerWithHostnameAsync(engine, connection, staleHostname);
            await InsertMarkerAndWaitForPrimaryAsync(connection, "before-recreate");
            await engine.DestroyAsync(connection.ContainerName);

            // Phase B: the broker recreates the container with the STABLE hostname. The persisted
            // config still names the stale host, so no primary can be elected until the broker
            // reconciles the single member onto the stable host.
            var broker = new MongoDbConnectionBroker();
            var client = await broker.GetClientAsync(connection);
            var collection = client.GetDatabase(connection.DatabaseName)
                .GetCollection<BsonDocument>(connection.CollectionName);

            // The pre-existing document survives (same /data/db) and a fresh write now succeeds,
            // proving a writable primary was elected after the forced reconfig.
            var preserved = await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", "before-recreate"));
            Assert.Equal(1, preserved);

            await collection.InsertOneAsync(new BsonDocument { { "_id", "after-reconfig" }, { "value", 1 } });
            var total = await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
            Assert.Equal(2, total);
        }
        finally
        {
            await TryDestroyAsync(engine, connection.ContainerName);
            TryDeleteDirectory(dataDirectory);
        }
    }

    // #1415: with a stable hostname, recreating the container across a (moving :latest) image refresh
    // reuses the persisted /data/db whose member host still matches, so the node comes back as a
    // writable primary without losing data.
    [Fact]
    public async Task EnsureContainerStarted_AfterImageRefresh_RemainsWritable()
    {
        var identifier = $"pw-mongo-1415-refresh-{Guid.NewGuid():N}".Substring(0, 40);
        var dataDirectory = Path.Combine(Path.GetTempPath(), identifier);
        var connection = new MongoDbContainerConnectionDefinition
        {
            ContainerName = identifier,
            DataDirectory = dataDirectory,
            DatabaseName = "pw_1415_db",
            CollectionName = "pw_1415_collection",
            HostPort = 37047,
        };
        var engine = new WindowsDockerDesktopEngine(NullLogger<DockerCommandRunner>.Instance);

        try
        {
            // Create + start via the broker (stable hostname) and write data.
            var broker = new MongoDbConnectionBroker();
            var client = await broker.GetClientAsync(connection);
            var collection = client.GetDatabase(connection.DatabaseName)
                .GetCollection<BsonDocument>(connection.CollectionName);
            await collection.InsertOneAsync(new BsonDocument { { "_id", "pre-refresh" }, { "value", 1 } });

            // Simulate an image refresh: destroy the container but keep /data/db. The broker's start
            // path fails and recreates the container with the SAME stable hostname.
            await engine.DestroyAsync(connection.ContainerName);

            var brokerAfterRefresh = new MongoDbConnectionBroker();
            var clientAfterRefresh = await brokerAfterRefresh.GetClientAsync(connection);
            var collectionAfterRefresh = clientAfterRefresh.GetDatabase(connection.DatabaseName)
                .GetCollection<BsonDocument>(connection.CollectionName);

            var preserved = await collectionAfterRefresh.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", "pre-refresh"));
            Assert.Equal(1, preserved);

            // A fresh write confirms the recreated node is a writable primary.
            await collectionAfterRefresh.InsertOneAsync(new BsonDocument { { "_id", "post-refresh" }, { "value", 2 } });
            var total = await collectionAfterRefresh.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
            Assert.Equal(2, total);
        }
        finally
        {
            await TryDestroyAsync(engine, connection.ContainerName);
            TryDeleteDirectory(dataDirectory);
        }
    }

    private static async Task StartContainerWithHostnameAsync(
        ContainerEngine engine,
        MongoDbContainerConnectionDefinition connection,
        string hostname)
    {
        var baseDefinition = new MongoDbContainerDefinitionGenerator().Generate(connection);
        var definition = new ContainerDefinition
        {
            ContainerName = baseDefinition.ContainerName,
            Hostname = hostname,
            ImageName = baseDefinition.ImageName,
            NetworkType = baseDefinition.NetworkType,
            EnvironmentVariables = baseDefinition.EnvironmentVariables,
            Mounts = baseDefinition.Mounts,
            PortMappings = baseDefinition.PortMappings,
        };

        await engine.CreateAsync(definition);
        await engine.StartAsync(connection.ContainerName);
    }

    private static async Task InsertMarkerAndWaitForPrimaryAsync(
        MongoDbContainerConnectionDefinition connection,
        string markerId)
    {
        // A write with a directConnection client blocks until the node is a writable primary (up to
        // the server-selection timeout), so it doubles as a "wait for primary" without polling.
        var settings = MongoClientSettings.FromConnectionString(
            $"mongodb://localhost:{connection.HostPort}/?directConnection=true");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(90);
        settings.ConnectTimeout = TimeSpan.FromSeconds(30);
        var client = new MongoClient(settings);
        var collection = client.GetDatabase(connection.DatabaseName)
            .GetCollection<BsonDocument>(connection.CollectionName);

        await collection.InsertOneAsync(new BsonDocument { { "_id", markerId }, { "value", 1 } });
    }

    private static async Task TryDestroyAsync(ContainerEngine engine, string containerName)
    {
        try
        {
            await engine.DestroyAsync(containerName);
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the bind mount may briefly hold a handle.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
