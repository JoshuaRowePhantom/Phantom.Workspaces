using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Runs the shared queue/embeddings contract against a real Atlas Local MongoDB container. The
/// vector index is eventually consistent, so the vector query is polled until the stored embeddings
/// (set via UpdateEmbeddings) have synced.
/// </summary>
[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbDataAccessLayerQueueEmbeddingsContractTests : DataAccessLayerQueueEmbeddingsContractTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbDataAccessLayerQueueEmbeddingsContractTests(MongoDbTestDatabaseFixture fixture)
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
                // The Atlas search service or a freshly synced vector index may not be ready yet.
            }

            if (DateTime.UtcNow >= deadline)
            {
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
            || message.Contains("NOT_STARTED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("PENDING", StringComparison.OrdinalIgnoreCase)
            || message.Contains("BUILDING", StringComparison.OrdinalIgnoreCase)
            || message.Contains("while in state", StringComparison.OrdinalIgnoreCase);
    }
}
