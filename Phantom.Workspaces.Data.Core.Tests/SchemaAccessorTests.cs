using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class SchemaAccessorTests
{
    [Fact]
    public async Task ResolveSchemaByReferenceAsync_ResolvesStoredSchemaByName()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaId = new EntityId("2b8d9f41-5a3c-4f0e-9c1b-7d6e2f4a8c10");
        const string schemaName = "https://schemas.workspaces.phantom.to/tests/stored-resolution.json";

        var addStoredSchemaResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateSchemaChange(schemaId, null, schemaName, "string")));
        Assert.DoesNotContain(addStoredSchemaResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        // No request schemas are supplied, so the reference can only be resolved
        // by getting the stored schema entity by name from the store.
        var schemaAccessor = new SchemaAccessor(dataAccessLayer);

        var resolvedSchema = await schemaAccessor.ResolveSchemaByReferenceAsync(schemaName);
        Assert.NotNull(resolvedSchema);
        Assert.True(resolvedSchema.Value.TryGetProperty("schema", out var schemaNode));
        Assert.Equal("string", schemaNode.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_PrefersRequestSchemaOverStoredSchema()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaId = new EntityId("76ce3bc3-ff8f-4858-bf8b-f11165fdb7f3");
        const string schemaName = "https://schemas.workspaces.phantom.to/tests/request-precedence.json";

        var addStoredSchemaResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateSchemaChange(schemaId, null, schemaName, "string")));
        Assert.DoesNotContain(addStoredSchemaResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
        var storedSchemaSnapshot = Assert.Single(addStoredSchemaResult.EntityResults).CurrentEntity;
        Assert.NotNull(storedSchemaSnapshot);

        var request = CreateUpdateRequest(
            CreateSchemaChange(schemaId, storedSchemaSnapshot!.ConcurrencyTag, schemaName, "integer"));
        var baseAccessor = new SchemaAccessor(dataAccessLayer);
        var schemaAccessor = baseAccessor.CreateOverlayForRequest(request);

        var resolvedSchema = await schemaAccessor.ResolveSchemaByReferenceAsync(schemaName);
        Assert.NotNull(resolvedSchema);
        Assert.True(resolvedSchema.Value.TryGetProperty("schema", out var schemaNode));
        Assert.Equal("integer", schemaNode.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_IsSafeUnderConcurrentAccess()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaAccessor = new SchemaAccessor(dataAccessLayer);

        // Prime the loaded-schema caches once so every worker reaches the
        // per-reference cache read/write path that previously corrupted the
        // non-thread-safe Dictionary (InvalidOperationException at TryGetValue).
        _ = await schemaAccessor.ResolveSchemaByReferenceAsync("priming-reference");

        const int workerCount = 8;
        const int iterationsPerWorker = 25;
        using var startBarrier = new Barrier(workerCount);

        var workers = Enumerable.Range(0, workerCount)
            .Select(workerIndex => Task.Run(async () =>
            {
                // Release all workers simultaneously to maximise contention,
                // without relying on timing-based delays.
                startBarrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerWorker; iteration++)
                {
                    // Distinct references always miss the cache, forcing a write
                    // on every call so concurrent writers collide.
                    _ = await schemaAccessor.ResolveSchemaByReferenceAsync(
                        $"missing-schema-{workerIndex}-{iteration}");
                }
            }))
            .ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public async Task BuildSchemaRegistryAsync_IsSafeUnderConcurrentAccessAcrossInstances()
    {
        // Multiple SchemaAccessor instances building their registry simultaneously used to corrupt
        // NJsonSchema's process-wide static Dictionary inside JsonSchema.BuildImpl because the
        // per-instance registryGate did not serialise calls across instances.
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();

        const int workerCount = 8;
        using var startBarrier = new Barrier(workerCount);

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                var schemaAccessor = new SchemaAccessor(dataAccessLayer);
                startBarrier.SignalAndWait();
                var registry = await schemaAccessor.BuildSchemaRegistryAsync();
                Assert.NotNull(registry);
            }))
            .ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public async Task BuildSchemaRegistryAsync_ReturnsCachedRegistryInstance()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaAccessor = new SchemaAccessor(dataAccessLayer);

        var firstRegistry = await schemaAccessor.BuildSchemaRegistryAsync();
        var secondRegistry = await schemaAccessor.BuildSchemaRegistryAsync();

        Assert.Same(firstRegistry, secondRegistry);
    }

    [Fact]
    public async Task BuildSchemaRegistryAsync_IncludesSchemaByJsonSchemaType_WhenNotUnderJsonSchemasFolder()
    {
        // Schema entities are discovered for the registry by the "json-schema" entity type,
        // not by the "json-schemas" name folder, so a json-schema-tagged entity indexed only
        // under "entity-types" must still be loaded into the registry by its $id.
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaId = new EntityId("3a9c5e7b-1d2f-4c8a-9e0b-6f4d2a8c1b30");
        const string schemaUri = "https://schemas.workspaces.phantom.to/tests/type-indexed-only.json";

        var addResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                new EntityChange
                {
                    EntityId = schemaId,
                    Data = JsonDocument.Parse(
                        $$"""
                        {
                          "entity-id": "{{schemaId}}",
                          "entity-types": ["entity", "json-schema"],
                          "names": [["entity-types", "type-indexed-only-schema"]],
                          "schema": {
                            "$id": "{{schemaUri}}",
                            "type": "object",
                            "properties": {
                              "title": { "type": "string" }
                            }
                          }
                        }
                        """).RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                }));
        Assert.DoesNotContain(addResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        var schemaAccessor = new SchemaAccessor(dataAccessLayer);

        var registry = await schemaAccessor.BuildSchemaRegistryAsync();

        Assert.NotNull(registry.Get(new Uri(schemaUri, UriKind.Absolute)));
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_ConcurrentCallsForSameReference_IssuesExactlyOneDataAccessLayerRequest()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        var counting = new CountingGetDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(counting);

        // Warm up the preloaded schema index so every factory invocation goes to GetAsync.
        await schemaAccessor.EnsureSchemasLoadedAsync(CancellationToken.None);
        counting.ResetCount();

        const int workerCount = 8;
        using var startBarrier = new Barrier(workerCount);

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                startBarrier.SignalAndWait();
                return await schemaAccessor.ResolveSchemaByReferenceAsync("missing-reference-concurrent");
            }))
            .ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(1, counting.GetCount);
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_WhenFetchFails_EvictsKeySoNextCallRetries()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        var failOnce = new FailFirstGetDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(failOnce);
        await schemaAccessor.EnsureSchemasLoadedAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => schemaAccessor.ResolveSchemaByReferenceAsync("retry-reference"));

        // Second call must succeed; the key was evicted after the failure.
        var result = await schemaAccessor.ResolveSchemaByReferenceAsync("retry-reference");
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_WhenFetchSucceeds_SubsequentCallsReturnCachedTask()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        var counting = new CountingGetDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(counting);
        await schemaAccessor.EnsureSchemasLoadedAsync(CancellationToken.None);
        counting.ResetCount();

        var task1 = schemaAccessor.ResolveSchemaByReferenceAsync("cached-reference");
        await task1;

        var task2 = schemaAccessor.ResolveSchemaByReferenceAsync("cached-reference");
        await task2;

        Assert.Equal(1, counting.GetCount);
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_WhenSchemaNotFound_CachesNullResult()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        var counting = new CountingGetDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(counting);
        await schemaAccessor.EnsureSchemasLoadedAsync(CancellationToken.None);
        counting.ResetCount();

        var first = await schemaAccessor.ResolveSchemaByReferenceAsync("not-found-reference");
        var second = await schemaAccessor.ResolveSchemaByReferenceAsync("not-found-reference");

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, counting.GetCount);
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_OneCancellationDoesNotCancelOtherConcurrentCallers()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        var blockingDal = new BlockingGetDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(blockingDal);
        await schemaAccessor.EnsureSchemasLoadedAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();

        var caller1 = schemaAccessor.ResolveSchemaByReferenceAsync("blocking-reference", cts.Token);
        var caller2 = schemaAccessor.ResolveSchemaByReferenceAsync("blocking-reference", CancellationToken.None);

        // Wait until the factory (GetAsync) is in-flight, then cancel caller 1.
        await blockingDal.WaitForGetAsync;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller1);

        // Release the blocking GetAsync; caller 2 should succeed.
        blockingDal.Release();
        var result = await caller2;
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_FindsSchema_BySchemaIdUri()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        const string schemaUri = "https://schemas.workspaces.phantom.to/tests/find-by-uri.json";
        var schemaEntityId = new EntityId("10000000-0000-0000-0000-000000000001");

        await inner.UpdateAsync(CreateUpdateRequest(
            CreateSchemaChange(schemaEntityId, null, schemaUri, "string")));

        var schemaAccessor = new SchemaAccessor(inner);
        var resolved = await schemaAccessor.ResolveSchemaByReferenceAsync(schemaUri);

        Assert.NotNull(resolved);
        Assert.True(SchemaAccessor.TryGetSchemaPayloadId(resolved.Value, out var id));
        Assert.Equal(schemaUri, id);
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_FindsSchema_ByEntityTypeName()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        const string schemaUri = "https://schemas.workspaces.phantom.to/tests/find-by-name.json";
        const string entityTypeName = "find-by-name-type";
        var schemaEntityId = new EntityId("10000000-0000-0000-0000-000000000002");

        await inner.UpdateAsync(CreateUpdateRequest(
            CreateSchemaChangeWithEntityTypeName(schemaEntityId, schemaUri, entityTypeName)));

        var schemaAccessor = new SchemaAccessor(inner);
        var resolved = await schemaAccessor.ResolveSchemaByReferenceAsync(entityTypeName);

        Assert.NotNull(resolved);
        Assert.True(SchemaAccessor.TryGetSchemaPayloadId(resolved.Value, out var id));
        Assert.Equal(schemaUri, id);
    }

    [Fact]
    public void BuildSchemasByEntityName_IndexesSchemaBySchemaIdUri_InAdditionToEntityTypeName()
    {
        const string schemaUri = "https://schemas.workspaces.phantom.to/tests/index-both-keys.json";
        const string entityTypeName = "index-both-keys-type";
        var schemaEntityId = new EntityId("10000000-0000-0000-0000-000000000003");

        using var doc = JsonDocument.Parse($$"""
            {
              "entity-id": "{{schemaEntityId}}",
              "entity-types": ["entity", "entity-type", "json-schema"],
              "names": [
                ["json-schemas", "{{schemaUri}}"],
                ["entity-types", "{{entityTypeName}}"]
              ],
              "schema": {
                "$id": "{{schemaUri}}",
                "type": "object"
              }
            }
            """);
        var schemasById = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [schemaUri] = doc.RootElement.Clone(),
        };

        var result = SchemaAccessor.BuildSchemasByEntityName(schemasById);

        Assert.True(result.ContainsKey(schemaUri));
        Assert.True(result.ContainsKey(entityTypeName));
    }

    [Fact]
    public async Task ResolveSchemaByReferenceAsync_DoesNotDoExtraDalFetch_WhenSameSchemaAlreadyCachedUnderDifferentKey()
    {
        var inner = await CreatePopulatedDataAccessLayerAsync();
        const string schemaUri = "https://schemas.workspaces.phantom.to/tests/multi-key-cache.json";
        const string entityTypeName = "multi-key-cache-type";
        var schemaEntityId = new EntityId("10000000-0000-0000-0000-000000000004");

        await inner.UpdateAsync(CreateUpdateRequest(
            CreateSchemaChangeWithEntityTypeName(schemaEntityId, schemaUri, entityTypeName)));

        var counting = new CountingGetDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(counting);

        // Resolve by $id URI — loads from the preloaded index, no GetAsync.
        var byUri = await schemaAccessor.ResolveSchemaByReferenceAsync(schemaUri);
        var getCountAfterFirst = counting.GetCount;

        // Resolve by entity-type name — also found in preloaded index.
        var byName = await schemaAccessor.ResolveSchemaByReferenceAsync(entityTypeName);

        Assert.NotNull(byUri);
        Assert.NotNull(byName);
        Assert.Equal(getCountAfterFirst, counting.GetCount);
    }

    private static EntityChange CreateSchemaChangeWithEntityTypeName(
        EntityId schemaEntityId,
        string schemaUri,
        string entityTypeName)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{schemaEntityId}}",
              "entity-types": ["entity", "entity-type", "json-schema"],
              "names": [
                ["json-schemas", "{{schemaUri}}"],
                ["entity-types", "{{entityTypeName}}"]
              ],
              "schema": {
                "$id": "{{schemaUri}}",
                "type": "object",
                "properties": {
                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                },
                "required": ["entity-types"]
              }
            }
            """);
        return new EntityChange
        {
            EntityId = schemaEntityId,
            Data = document.RootElement.Clone(),
            EntityChangeMode = EntityChangeMode.Replace,
        };
    }

    private sealed class CountingGetDataAccessLayer : IDataAccessLayer
    {
        private readonly IDataAccessLayer _inner;
        private int _getCount;

        public CountingGetDataAccessLayer(IDataAccessLayer inner) => _inner = inner;

        public int GetCount => _getCount;
        public void ResetCount() => Interlocked.Exchange(ref _getCount, 0);

        public async Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _getCount);
            return await _inner.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(request, cancellationToken);

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(request, cancellationToken);

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => _inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => _inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => _inner.GetChangedEntitiesAsync(request, cancellationToken);
    }

    private sealed class FailFirstGetDataAccessLayer : IDataAccessLayer
    {
        private readonly IDataAccessLayer _inner;
        private int _callCount;

        public FailFirstGetDataAccessLayer(IDataAccessLayer inner) => _inner = inner;

        public async Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                throw new InvalidOperationException("Simulated first-call failure.");
            }

            return await _inner.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(request, cancellationToken);

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(request, cancellationToken);

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => _inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => _inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => _inner.GetChangedEntitiesAsync(request, cancellationToken);
    }

    private sealed class BlockingGetDataAccessLayer : IDataAccessLayer
    {
        private readonly IDataAccessLayer _inner;
        private readonly TaskCompletionSource _getStarted = new();
        private readonly TaskCompletionSource _releaseGate = new();

        public BlockingGetDataAccessLayer(IDataAccessLayer inner) => _inner = inner;

        public Task WaitForGetAsync => _getStarted.Task;

        public void Release() => _releaseGate.TrySetResult();

        public async Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            _getStarted.TrySetResult();
            await _releaseGate.Task.ConfigureAwait(false);
            return await _inner.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(request, cancellationToken);

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(request, cancellationToken);

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => _inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => _inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => _inner.GetChangedEntitiesAsync(request, cancellationToken);
    }

    private static async Task<IDataAccessLayer> CreatePopulatedDataAccessLayerAsync()
    {
        var underlying = new InMemoryDataAccessLayer();
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(new ReferentialIntegrityDataAccessLayer(underlying));
        var populator = new SchemaPopulator(dataAccessLayer);
        var errors = await populator.Populate();
        Assert.Empty(errors);
        return dataAccessLayer;
    }

    private static UpdateRequest CreateUpdateRequest(EntityChange change)
    {
        return new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown
                {
                    Text = "SchemaAccessor test update.",
                },
            },
            Changes = [change],
        };
    }

    private static EntityChange CreateSchemaChange(
        EntityId schemaEntityId,
        ConcurrencyTag? concurrencyTag,
        string schemaName,
        string titleType)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{schemaEntityId}}",
              "entity-types": ["entity", "entity-type", "json-schema"],
              "names": [["json-schemas", "{{schemaName}}"]],
              "schema": {
                "$id": "{{schemaName}}",
                "type": "object",
                "properties": {
                  "title": { "type": "{{titleType}}" },
                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                },
                "required": ["title", "entity-types"]
              }
            }
            """);
        return new EntityChange
        {
            EntityId = schemaEntityId,
            ConcurrencyTag = concurrencyTag,
            Data = document.RootElement.Clone(),
            EntityChangeMode = EntityChangeMode.Replace,
        };
    }
}

