using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;
using Phantom.Workspaces.Data.MongoDB.Tests;

namespace Phantom.Workspaces.Tests;

[CollectionDefinition(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbTestDatabaseCollectionFixture : ICollectionFixture<EntityRepositoryMongoDbFixture> { }

public sealed class EntityRepositoryMongoDbFixture : IAsyncLifetime
{
    public MongoDbTestDatabaseFixture DatabaseFixture { get; } = new();

    public ValueTask InitializeAsync() => new(this.DatabaseFixture.InitializeAsync());

    public ValueTask DisposeAsync() => new(this.DatabaseFixture.DisposeAsync());
}

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class EntityRepositoryMongoDbSlowTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public EntityRepositoryMongoDbSlowTests(EntityRepositoryMongoDbFixture fixture)
    {
        _fixture = fixture.DatabaseFixture;
    }

    private Task<EntityRepository> CreateRepositoryAsync(
        Func<IMongoDatabase, string, MongoDbEntityDataAccessLayer>? layerFactory = null)
    {
        var source = _fixture.ConnectionDefinition is MongoDbContainerConnectionDefinition container
            ? new MongoDbRepositorySource(container.ContainerName, container.CollectionName,
                container.DataDirectory, container.DatabaseName, container.HostPort)
            : new MongoDbRepositorySource("fixture-owned-external-client", MongoDbTestDatabaseFixture.EntityCollectionName,
                DatabaseName: _fixture.Database.DatabaseNamespace.DatabaseName);
        return EntityRepository.CreateAsync(new EntityRepositoryInitializationOptions
        {
            RepositorySource = source,
            MongoDbClientFactory = _fixture.ConnectionDefinition is MongoDbExternalConnectionDefinition
                ? (_, _) => ValueTask.FromResult(_fixture.Database.Client)
                : null,
            MongoDbEntityDataAccessLayerFactory = layerFactory,
        }, TestContext.Current.CancellationToken);
    }

    private static BsonDocument CreatePreV760Doc(string entityId) => new()
    {
        { "_id", entityId },
        { "versions", new BsonArray() },
        {
            "current", new BsonDocument
            {
                {
                    "data", new BsonDocument
                    {
                        { "entity-id", entityId },
                        { "entity-types", new BsonArray { "entity" } },
                        { "names", new BsonArray { new BsonArray { new BsonString("workspace"), new BsonString("test-entity") } } },
                    }
                },
                { "type-names", new BsonArray { "entity" } },
                { "names", new BsonArray { new BsonArray { new BsonString("workspace"), new BsonString("test-entity") } } },
                { "is-deleted", false },
                { "modified-time-utc", DateTime.UtcNow },
                { "modified-version", "000000000000000000000000" },
            }
        },
    };

    [Fact]
    public async Task EntityRepository_CreateAsync_WithPreMigrationDatabase_DoesNotThrow()
    {
        await _fixture.ResetCollectionAsync();

        var entityId = Guid.NewGuid().ToString();
        await _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities")
            .InsertOneAsync(CreatePreV760Doc(entityId), cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var repository = await CreateRepositoryAsync();
        });
        Assert.Null(exception);
    }

    [Fact]
    public async Task EntityRepository_CreateAsync_WithPreMigrationDatabase_EntitiesAreReadableAfterStartup()
    {
        await _fixture.ResetCollectionAsync();

        var capturedEntityId = Guid.NewGuid().ToString();
        await _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities")
            .InsertOneAsync(CreatePreV760Doc(capturedEntityId), cancellationToken: TestContext.Current.CancellationToken);
        await using var repository = await CreateRepositoryAsync();
        var snapshots = await repository.ExportEntitySnapshotsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(new EntityId(Guid.Parse(capturedEntityId)), snapshots.Keys);
    }

    [Fact]
    public async Task EntityRepository_CreateAsync_CallsEnsureIndexesAsyncBeforeMigrateAsync()
    {
        await _fixture.ResetCollectionAsync();

        InstrumentedMongoDbEntityDataAccessLayer dal = null!;
        await using var repository = await CreateRepositoryAsync((database, collectionName) =>
        {
            dal = new InstrumentedMongoDbEntityDataAccessLayer(database, collectionName);
            return dal;
        });
        var callLog = dal.CallLog.ToList();
        var ensureIndex = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.EnsureIndexesAsync));
        var migrate = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.MigrateAsync));
        Assert.True(ensureIndex >= 0, "EnsureIndexesAsync should have been called");
        Assert.True(migrate >= 0, "MigrateAsync should have been called");
        Assert.True(ensureIndex < migrate, "EnsureIndexesAsync should be called before MigrateAsync");
    }

    [Fact]
    public async Task EntityRepository_CreateAsync_CallsMigrateAsyncBeforeAnyGetAsync()
    {
        await _fixture.ResetCollectionAsync();

        InstrumentedMongoDbEntityDataAccessLayer dal = null!;
        await using var repository = await CreateRepositoryAsync((database, collectionName) =>
        {
            dal = new InstrumentedMongoDbEntityDataAccessLayer(database, collectionName);
            return dal;
        });
        var callLog = dal.CallLog.ToList();
        var migrate = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.MigrateAsync));
        var getAsync = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.GetAsync));
        Assert.True(migrate >= 0, "MigrateAsync should have been called");
        Assert.True(getAsync >= 0, "GetAsync should have been called");
        Assert.True(migrate < getAsync, "MigrateAsync should be called before any GetAsync");
    }

    private sealed class InstrumentedMongoDbEntityDataAccessLayer : MongoDbEntityDataAccessLayer
    {
        private readonly List<string> _callLog = [];
        public IReadOnlyList<string> CallLog => _callLog;

        public InstrumentedMongoDbEntityDataAccessLayer(IMongoDatabase database, string collectionName)
            : base(database, collectionName) { }

        public override async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            _callLog.Add(nameof(EnsureIndexesAsync));
            await base.EnsureIndexesAsync(cancellationToken);
        }

        public override async Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            _callLog.Add(nameof(MigrateAsync));
            await base.MigrateAsync(cancellationToken);
        }

        public override async Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            _callLog.Add(nameof(GetAsync));
            return await base.GetAsync(request, cancellationToken);
        }
    }
}
