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
        var schemaAccessor = new SchemaAccessor(dataAccessLayer, request);

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

        const int workerCount = 32;
        const int iterationsPerWorker = 200;
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
    public async Task BuildSchemaRegistryAsync_ReturnsCachedRegistryInstance()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaAccessor = new SchemaAccessor(dataAccessLayer);

        var firstRegistry = await schemaAccessor.BuildSchemaRegistryAsync();
        var secondRegistry = await schemaAccessor.BuildSchemaRegistryAsync();

        Assert.Same(firstRegistry, secondRegistry);
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

    private static UpdateRequest CreateUpdateRequest(
        EntityChange change)
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
        using var document = JsonDocument.Parse(
            $$"""
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

