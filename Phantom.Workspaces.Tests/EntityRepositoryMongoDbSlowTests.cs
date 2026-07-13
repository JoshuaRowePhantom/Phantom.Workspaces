using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;
using Phantom.Workspaces.Data.MongoDB.Tests;

namespace Phantom.Workspaces.Tests;

[CollectionDefinition(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbTestDatabaseCollectionFixture : ICollectionFixture<MongoDbTestDatabaseFixture> { }

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class EntityRepositoryMongoDbSlowTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public EntityRepositoryMongoDbSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetCollectionAsync().GetAwaiter().GetResult();
    }

    private MongoDbRepositorySource CreateMongoDbRepositorySource() => new(
        ContainerName: MongoDbTestDatabaseFixture.ContainerName,
        RootCollectionName: MongoDbTestDatabaseFixture.EntityCollectionName,
        DataDirectory: _fixture.ConnectionDefinition.DataDirectory,
        DatabaseName: MongoDbTestDatabaseFixture.DatabaseName,
        HostPort: MongoDbTestDatabaseFixture.HostPort);

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

        EntityRepository.TestMongoDbEntityDataAccessLayerFactory = (database, collectionName) =>
        {
            var entityId = Guid.NewGuid().ToString();
            var collection = database.GetCollection<BsonDocument>($"{collectionName}_entities");
            collection.InsertOneAsync(CreatePreV760Doc(entityId)).GetAwaiter().GetResult();
            return new MongoDbEntityDataAccessLayer(database, collectionName);
        };
        try
        {
            var exception = await Record.ExceptionAsync(() => EntityRepository.CreateAsync(CreateMongoDbRepositorySource()));
            Assert.Null(exception);
        }
        finally
        {
            EntityRepository.TestMongoDbEntityDataAccessLayerFactory = null;
        }
    }

    [Fact]
    public async Task EntityRepository_CreateAsync_WithPreMigrationDatabase_EntitiesAreReadableAfterStartup()
    {
        await _fixture.ResetCollectionAsync();

        string capturedEntityId = null!;
        EntityRepository.TestMongoDbEntityDataAccessLayerFactory = (database, collectionName) =>
        {
            capturedEntityId = Guid.NewGuid().ToString();
            var collection = database.GetCollection<BsonDocument>($"{collectionName}_entities");
            collection.InsertOneAsync(CreatePreV760Doc(capturedEntityId)).GetAwaiter().GetResult();
            return new MongoDbEntityDataAccessLayer(database, collectionName);
        };
        try
        {
            var repository = await EntityRepository.CreateAsync(CreateMongoDbRepositorySource());
            var snapshots = await repository.ExportEntitySnapshotsAsync(TestContext.Current.CancellationToken);
            Assert.Contains(new EntityId(Guid.Parse(capturedEntityId)), snapshots.Keys);
        }
        finally
        {
            EntityRepository.TestMongoDbEntityDataAccessLayerFactory = null;
        }
    }

    [Fact]
    public async Task EntityRepository_CreateAsync_CallsEnsureIndexesAsyncBeforeMigrateAsync()
    {
        await _fixture.ResetCollectionAsync();

        InstrumentedMongoDbEntityDataAccessLayer dal = null!;
        EntityRepository.TestMongoDbEntityDataAccessLayerFactory = (database, collectionName) =>
        {
            dal = new InstrumentedMongoDbEntityDataAccessLayer(database, collectionName);
            return dal;
        };
        try
        {
            await EntityRepository.CreateAsync(CreateMongoDbRepositorySource());

            var callLog = dal.CallLog.ToList();
            var ensureIndex = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.EnsureIndexesAsync));
            var migrate = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.MigrateAsync));
            Assert.True(ensureIndex >= 0, "EnsureIndexesAsync should have been called");
            Assert.True(migrate >= 0, "MigrateAsync should have been called");
            Assert.True(ensureIndex < migrate, "EnsureIndexesAsync should be called before MigrateAsync");
        }
        finally
        {
            EntityRepository.TestMongoDbEntityDataAccessLayerFactory = null;
        }
    }

    [Fact]
    public async Task EntityRepository_CreateAsync_CallsMigrateAsyncBeforeAnyGetAsync()
    {
        await _fixture.ResetCollectionAsync();

        InstrumentedMongoDbEntityDataAccessLayer dal = null!;
        EntityRepository.TestMongoDbEntityDataAccessLayerFactory = (database, collectionName) =>
        {
            dal = new InstrumentedMongoDbEntityDataAccessLayer(database, collectionName);
            return dal;
        };
        try
        {
            await EntityRepository.CreateAsync(CreateMongoDbRepositorySource());

            var callLog = dal.CallLog.ToList();
            var migrate = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.MigrateAsync));
            var getAsync = callLog.IndexOf(nameof(MongoDbEntityDataAccessLayer.GetAsync));
            Assert.True(migrate >= 0, "MigrateAsync should have been called");
            Assert.True(getAsync >= 0, "GetAsync should have been called");
            Assert.True(migrate < getAsync, "MigrateAsync should be called before any GetAsync");
        }
        finally
        {
            EntityRepository.TestMongoDbEntityDataAccessLayerFactory = null;
        }
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
