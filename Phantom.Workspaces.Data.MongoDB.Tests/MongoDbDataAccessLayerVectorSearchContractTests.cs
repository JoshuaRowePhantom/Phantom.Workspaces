using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Runs the shared vector-search contract against a real Atlas Local MongoDB container. Atlas
/// vector search indexes are eventually consistent, so the query is polled until the index has
/// synced the seeded documents.
/// </summary>
[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbDataAccessLayerVectorSearchContractTests : DataAccessLayerVectorSearchContractTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbDataAccessLayerVectorSearchContractTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetCollectionAsync().GetAwaiter().GetResult();
    }

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
    }

    protected override async Task<IReadOnlyList<QueryEntitySnapshot>> RunVectorQueryAsync(
        IDataAccessLayer dataAccessLayer,
        QueryRequest request)
    {
        var mongo = (MongoDbEntityDataAccessLayer)dataAccessLayer;

        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            try
            {
                // The Atlas Local search service (mongot) can take additional time to become
                // available after mongod reports healthy, and a freshly created vector index needs
                // to sync before it returns results, so tolerate both while polling.
                await mongo.EnsureVectorIndexAsync();

                var result = await dataAccessLayer.QueryAsync(request);
                var entities = Assert.Single(result.Batches).Entities.ToArray();
                if (entities.Length > 0)
                {
                    return entities;
                }
            }
            catch (global::MongoDB.Driver.MongoCommandException exception)
                when (IsVectorIndexNotReady(exception))
            {
                // The Atlas Local search service (mongot) may not be ready yet, or a freshly created
                // vector index may still be initializing/syncing the seeded documents; keep polling.
            }

            if (DateTime.UtcNow >= deadline)
            {
                // One final attempt so the assertion failure (if any) is meaningful.
                var result = await dataAccessLayer.QueryAsync(request);
                return Assert.Single(result.Batches).Entities.ToArray();
            }

            await Task.Delay(PollInterval);
        }
    }

    private static bool IsVectorIndexNotReady(
        global::MongoDB.Driver.MongoCommandException exception)
    {
        var message = exception.Message;
        return message.Contains("Search Index", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not initialized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not ready", StringComparison.OrdinalIgnoreCase)
            // The Atlas vector index reports a transient build state while it syncs documents; the
            // exact wording varies by phase ("while in state NOT_STARTED/PENDING/BUILDING").
            || message.Contains("NOT_STARTED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("PENDING", StringComparison.OrdinalIgnoreCase)
            || message.Contains("BUILDING", StringComparison.OrdinalIgnoreCase)
            || message.Contains("while in state", StringComparison.OrdinalIgnoreCase);
    }
}
