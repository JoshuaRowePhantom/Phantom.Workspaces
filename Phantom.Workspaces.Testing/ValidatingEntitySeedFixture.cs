using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Testing;

/// <summary>
/// Seeds runtime entity fixtures through the same validating pipeline used by production.
/// </summary>
public sealed class ValidatingEntitySeedFixture
{
    private readonly InMemoryDataAccessLayer rawStore;

    private ValidatingEntitySeedFixture(
        InMemoryDataAccessLayer rawStore,
        IDataAccessLayer dataAccessLayer)
    {
        this.rawStore = rawStore;
        this.DataAccessLayer = dataAccessLayer;
    }

    public IDataAccessLayer DataAccessLayer { get; }

    public static async Task<ValidatingEntitySeedFixture> CreateAsync(
        CancellationToken cancellationToken = default)
        => await CreateAsync(new InMemoryDataAccessLayer(), cancellationToken).ConfigureAwait(false);

    public static async Task<ValidatingEntitySeedFixture> CreateAsync(
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return await CreateAsync(
            new InMemoryDataAccessLayer(timeProvider: timeProvider),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ValidatingEntitySeedFixture> CreateAsync(
        InMemoryDataAccessLayer rawStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataAccessLayer = ValidatedDataAccessLayerFactory.Create(rawStore);
        var populateErrors = await new SchemaPopulator(dataAccessLayer).Populate().ConfigureAwait(false);
        if (populateErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"The production schema corpus could not be populated:{Environment.NewLine}"
                + string.Join(Environment.NewLine, populateErrors.Select(static error => $"- {error.Message}")));
        }

        return new ValidatingEntitySeedFixture(rawStore, dataAccessLayer);
    }

    public async Task<EntityId> SeedValidEntityAsync(
        JsonElement entity,
        CancellationToken cancellationToken = default)
    {
        var entityIds = await this.SeedManyValidAsync([entity], cancellationToken).ConfigureAwait(false);
        return entityIds[0];
    }

    public async Task<IReadOnlyList<EntityId>> SeedManyValidAsync(
        IEnumerable<JsonElement> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var changes = entities.Select(CreateValidEntityChange).ToArray();
        if (changes.Length == 0)
        {
            return [];
        }

        var result = await this.DataAccessLayer.UpdateAsync(
            CreateUpdateRequest("Seed schema-valid test entities.", changes),
            cancellationToken).ConfigureAwait(false);
        ThrowIfUpdateFailed(result, "Schema-valid test entity seeding failed");
        return changes.Select(static change => change.EntityId!.Value).ToArray();
    }

    /// <summary>
    /// Writes deliberately malformed input below validation. Use only when validation rejection is the test subject.
    /// </summary>
    public async Task SeedRawEntityForMalformedInputTestAsync(
        JsonElement entity,
        CancellationToken cancellationToken = default)
    {
        var change = CreateEntityChange(entity);
        var result = await this.rawStore.UpdateAsync(
            CreateUpdateRequest("Seed deliberately malformed test input below validation.", [change]),
            cancellationToken).ConfigureAwait(false);
        ThrowIfUpdateFailed(result, "Raw malformed-input test seeding failed");
    }

    private static EntityChange CreateValidEntityChange(JsonElement entity)
    {
        if (!entity.TryGetProperty("entity-types", out var entityTypes)
            || entityTypes.ValueKind != JsonValueKind.Array
            || !entityTypes.EnumerateArray().Any(
                static item => item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), "entity", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A valid persisted fixture must declare the base 'entity' type.",
                nameof(entity));
        }

        return CreateEntityChange(entity);
    }

    private static EntityChange CreateEntityChange(JsonElement entity)
    {
        if (entity.ValueKind != JsonValueKind.Object
            || !entity.TryGetProperty("entity-id", out var entityIdProperty)
            || entityIdProperty.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entityIdProperty.GetString(), out var entityId))
        {
            throw new ArgumentException(
                "A persisted fixture must contain a valid 'entity-id'.",
                nameof(entity));
        }

        return new EntityChange
        {
            EntityId = new EntityId(entityId),
            EntityChangeMode = EntityChangeMode.Replace,
            Data = entity.Clone(),
        };
    }

    private static UpdateRequest CreateUpdateRequest(
        string comment,
        IReadOnlyCollection<EntityChange> changes)
        => new()
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = comment },
            },
            Changes = changes,
        };

    private static void ThrowIfUpdateFailed(
        UpdateResult result,
        string message)
    {
        var failures = result.EntityResults
            .Where(static entityResult =>
                entityResult.UpdateState == UpdateState.Failed || entityResult.Errors.Count > 0)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        var details = failures.SelectMany(
            static entityResult => entityResult.Errors.Count == 0
                ? [$"- {entityResult.RequestedEntityId}: update state was {entityResult.UpdateState}."]
                : entityResult.Errors.Select(
                    error => $"- {entityResult.RequestedEntityId}: {error.Message}"));
        throw new InvalidOperationException(
            $"{message}:{Environment.NewLine}{string.Join(Environment.NewLine, details)}");
    }
}
