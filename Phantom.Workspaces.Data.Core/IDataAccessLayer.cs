using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data;

public interface IDataAccessLayer
{
    /// <summary>
    /// Perform Create, Update, Delete operations on entities as a single transaction.
    /// If any entity change fails, the entire transaction will be aborted and no changes will be applied.
    /// </summary>
    Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve a set of entity snapshots by their ids, as of the specified timestamps. 
    /// </summary>
    Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Query for entities.
    /// </summary>
    Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);

    Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default);

    [Obsolete("ExportAsync is very expensive and should only be used for full enumeration in rare cases.")]
    Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the entities that have changed since their corresponding timestamps.
    /// </summary>
    /// <returns>
    /// The set of entities that have changed since the provided timestamps, along with their current snapshots.
    /// If an entity has changed because a relationship was added or removed, the added or removed relationship will
    /// be returned, but the entity itself will only be returned if the entity's own data has changed.
    /// </returns>
    Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a named queue of recently-changed entities for decoupled background work (for
    /// example, vector indexing). A queue's head is the timestamp of the last entity processed.
    /// When <see cref="ProcessQueueRequest.Token"/> is supplied, the persisted head is first
    /// advanced to it (acknowledging the previous batch); the method then returns up to
    /// <see cref="ProcessQueueRequest.Count"/> entities in modified-timestamp order after the head,
    /// plus the token the caller should pass next time. Not all data access layers support queues.
    /// </summary>
    Task<ProcessQueueResult> ProcessQueueAsync(
        ProcessQueueRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This data access layer does not support queue processing.");

    /// <summary>
    /// Computes embedding vectors for the supplied entity snapshots using the configured embeddings
    /// provider. Not all data access layers support embeddings.
    /// </summary>
    Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(
        ComputeEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This data access layer does not support embedding computation.");

    /// <summary>
    /// Stores (or, when an update carries no values, clears) embedding vectors for entities, keyed
    /// by entity id, so they are used by vector search. Not all data access layers support
    /// embeddings.
    /// </summary>
    Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(
        UpdateEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This data access layer does not support embedding updates.");
}

public sealed record UpdateRequest
{
    [JsonPropertyName("update-metadata")]
    public required UpdateMetadata UpdateMetadata { get; init; }

    [JsonPropertyName("changes")]
    public required IReadOnlyCollection<EntityChange> Changes { get; init; }
}

public sealed record Markdown
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record UpdateMetadata
{
    [JsonPropertyName("comment")]
    public required Markdown Comment { get; init; }
}

public sealed record EntityChange
{
    [JsonPropertyName("entity-id")]
    public EntityId? EntityId { get; init; }

    [JsonPropertyName("concurrency-tag")]
    public ConcurrencyTag? ConcurrencyTag { get; init; }

    // null to remove the entity.
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("entity-change-mode")]
    public required EntityChangeMode EntityChangeMode { get; init; }
}

public sealed record UpdateResult
{
    [JsonPropertyName("entity-results")]
    public required IReadOnlyCollection<EntityUpdateResult> EntityResults { get; init; }
}

public sealed record EntityUpdateResult
{
    [JsonPropertyName("update-state")]
    public required UpdateState UpdateState { get; init; }

    [JsonPropertyName("requested-entity-id")]
    public required EntityId RequestedEntityId { get; init; }

    [JsonPropertyName("resulting-entity-id")]
    public required EntityId ResultingEntityId { get; init; }

    [JsonPropertyName("concurrency-tag")]
    public ConcurrencyTag? ConcurrencyTag { get; init; }

    [JsonPropertyName("concurrency-match-state")]
    public required ConcurrencyMatchState ConcurrencyMatchState { get; init; }

    [JsonPropertyName("current-entity")]
    public EntitySnapshot? CurrentEntity { get; init; }

    [JsonPropertyName("errors")]
    public required IReadOnlyCollection<UpdateError> Errors { get; init; }
}

public sealed record UpdateError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    // This is set if there is a related entity id causing the failure.
    [JsonPropertyName("related-entity-id")]
    public EntityId? RelatedEntityId { get; init; }
}

public sealed record GetRequest
{
    [JsonPropertyName("get-entity")]
    public required IReadOnlyCollection<GetEntityRequest> Entities { get; init; }

    [JsonPropertyName("properties")]
    public IReadOnlyCollection<string>? Properties { get; init; }

    // null means do not return relationships, empty means return all, non-empty means return matching relationships.
    [JsonPropertyName("relationships-to-return")]
    public IReadOnlyCollection<GetRelationshipRequest>? RelationshipsToReturn { get; init; }

    [JsonPropertyName("timestamps")]
    public IReadOnlyCollection<Timestamp?>? Timestamps { get; init; }
}

public sealed record GetEntityRequest
{
    [JsonPropertyName("entity-id")]
    public EntityId? EntityId { get; init; }

    [JsonPropertyName("entity-name")]
    public EntityName? EntityName { get; init; }

    [JsonPropertyName("enumerate-children")]
    public EnumerateChildrenAction EnumerateChildren { get; init; } = EnumerateChildrenAction.EnumerateSelf;

    [JsonPropertyName("entity-type-names")]
    public EntityTypeNameSet? EntityTypeNames { get; init; }

    [JsonPropertyName("properties")]
    public IReadOnlyCollection<string>? Properties { get; init; }

    // null means inherit request-level value; empty means return all; non-empty means return matching relationships.
    [JsonPropertyName("relationships-to-return")]
    public IReadOnlyCollection<GetRelationshipRequest>? RelationshipsToReturn { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<EnumerateChildrenAction>))]
public enum EnumerateChildrenAction
{
    [JsonStringEnumMemberName("self")]
    EnumerateSelf = 0,

    [JsonStringEnumMemberName("children")]
    EnumerateChildren = 1,

    [JsonStringEnumMemberName("all-children")]
    EnumerateAllChildren = 2,
}

public sealed record GetRelationshipRequest
{
    [JsonPropertyName("relationship-type-names")]
    public RelationshipTypeNameSet? RelationshipTypeNames { get; init; }

    [JsonPropertyName("relationship-role-names")]
    public RoleNameSet? RelationshipRoleNames { get; init; }
}

public sealed record GetResult
{
    [JsonPropertyName("batches")]
    public required IReadOnlyCollection<TimestampedEntityBatch> Batches { get; init; }
}

/// <summary>
/// A request to query for entities. 
/// </summary>
/// <param name="Clauses">
/// The set of clauses to query for. Each clause can produce a set of entities.
/// </param>
/// <param name="Timestamps">
/// The set of timestamps to query as-of. null means "now".
/// </param>
public sealed record QueryRequest
{
    [JsonPropertyName("clauses")]
    public required IReadOnlyCollection<TopLevelQueryClause> Clauses { get; init; }

    [JsonPropertyName("timestamps")]
    public IReadOnlyCollection<Timestamp?>? Timestamps { get; init; }

    /// <summary>
    /// Optional relationship filters; when set, each matched entity's <see cref="EntitySnapshot.Relationships"/>
    /// is populated with the relationships it participates in that match these filters (an empty filter
    /// matches all). Used, for example, so views can render an entity's interest badges from its relationships.
    /// </summary>
    [JsonPropertyName("relationships-to-return")]
    public IReadOnlyCollection<GetRelationshipRequest>? RelationshipsToReturn { get; init; }
}

public sealed record QueryResult
{
    [JsonPropertyName("batches")]
    public required IReadOnlyCollection<TimestampedQueryBatch> Batches { get; init; }
}

public sealed record GetHistoryRequest
{
    [JsonPropertyName("entity-ids")]
    public required IReadOnlyCollection<EntityId> EntityIds { get; init; }
}

public sealed record GetHistoryResult
{
    [JsonPropertyName("history")]
    public required IReadOnlyCollection<EntityHistoryEntry> History { get; init; }
}

/// <summary>
/// Requests a full export of all entities at or after an optional snapshot time.
/// This API is intentionally expensive and should only be used when enumerating everything is unavoidable.
/// </summary>
public sealed record ExportRequest
{
    [JsonPropertyName("snapshot-time")]
    public Timestamp? SnapshotTime { get; init; }
}

/// <summary>
/// A full export of all entities.
/// This result is intentionally expensive to produce and should only be consumed in rare enumeration scenarios.
/// </summary>
public sealed record ExportResult
{
    [JsonPropertyName("change-batches")]
    public required IReadOnlyCollection<ExportChangeBatch> ChangeBatches { get; init; }

    [JsonPropertyName("final-snapshot-time")]
    public required Timestamp FinalSnapshotTime { get; init; }
}

public sealed record GetChangedEntitiesRequest
{
    [JsonPropertyName("entity-id-timestamps")]
    public required IReadOnlyCollection<EntityIdTimestamp> EntityIdTimestamps { get; init; }
}

public sealed record GetChangedEntitiesResult
{
    [JsonPropertyName("entities")]
    public required IReadOnlyCollection<ChangedEntitySnapshot> Entities { get; init; }
}

public sealed record TimestampedEntityBatch
{
    [JsonPropertyName("timestamp")]
    public Timestamp? Timestamp { get; init; }

    [JsonPropertyName("entities")]
    public required IReadOnlyCollection<EntitySnapshot> Entities { get; init; }
}

/// <summary>
/// A timestamp-specific batch of query results. 
/// Each batch corresponds to a specific timestamp, and contains the entities that match the query as of that timestamp.
/// </summary>
public sealed record TimestampedQueryBatch
{
    [JsonPropertyName("timestamp")]
    public Timestamp? Timestamp { get; init; }

    [JsonPropertyName("entities")]
    public required IReadOnlyCollection<QueryEntitySnapshot> Entities { get; init; }
}

/// <summary>
/// An entity returned by a query.
/// </summary>
/// <param name="MatchingClauseIdentifiers">
/// The set of query clause identifiers that returned this entity.
/// </param>
public sealed record QueryEntitySnapshot : EntitySnapshot
{
    [JsonPropertyName("classified-time")]
    public Timestamp? ClassifiedTime { get; init; }

    [JsonPropertyName("matching-clause-identifiers")]
    public required IReadOnlyCollection<QueryClauseIdentifier> MatchingClauseIdentifiers { get; init; }

    [JsonPropertyName("vector-query-scores")]
    public IReadOnlyCollection<VectorQueryScore> VectorQueryScores { get; init; } = [];
}

/// <summary>
/// When an entity matches a Vector query, the VectorQueryScore contains the similarity score for
/// that match and the identifier of the corresponding vector query clause.
/// </summary>
public sealed record VectorQueryScore
{
    [JsonPropertyName("query-identifier")]
    public required QueryClauseIdentifier QueryIdentifier { get; init; }

    [JsonPropertyName("score")]
    public required double Score { get; init; }
}

public sealed record EntityHistoryEntry
{
    [JsonPropertyName("entity-id")]
    public required EntityId EntityId { get; init; }

    [JsonPropertyName("update-times")]
    public required IReadOnlyCollection<Timestamp> UpdateTimes { get; init; }
}

public sealed record ExportChangeBatch
{
    [JsonPropertyName("change-time")]
    public required Timestamp ChangeTime { get; init; }

    [JsonPropertyName("entities")]
    public required IReadOnlyCollection<QueryEntitySnapshot> Entities { get; init; }
}

public sealed record ChangedEntitySnapshot
{
    // The changed entity.
    [JsonPropertyName("entity")]
    public EntitySnapshot? Entity { get; init; }
}

/// <summary>
/// A snapshot of an entity's data.
/// </summary>
/// <param name="EntityId">
/// The id of the entity. This is required for all snapshots, even for deleted entities, to identify which entity the snapshot corresponds to.
/// </param>
/// <param name="ConcurrencyTag">
/// The concurrency tag of the entity. This value is only valid
/// if the entity is at its latest version. 
/// When updating an entity, the provided concurrency tag
/// must match the current concurrency tag of the entity.
/// </param>
/// <param name="ModifiedTime">
/// The data-access-layer timestamp of when the entity was last modified.
/// </param>
/// <param name="Data">
/// The data of the entity. This can be null if the entity has been deleted or if the data is not available.
/// </param>
public record EntitySnapshot
{
    [JsonPropertyName("entity-id")]
    public required EntityId EntityId { get; init; }

    [JsonPropertyName("concurrency-tag")]
    public ConcurrencyTag? ConcurrencyTag { get; init; }

    [JsonPropertyName("modified-time")]
    public required Timestamp ModifiedTime { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("relationships")]
    public required IReadOnlyCollection<EntitySnapshot> Relationships { get; init; }
}

public sealed record ProcessQueueRequest
{
    [JsonPropertyName("queue-name")]
    public required string QueueName { get; init; }

    [JsonPropertyName("token")]
    public Timestamp? Token { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

public sealed record ProcessQueueResult
{
    [JsonPropertyName("entities")]
    public required IReadOnlyList<EntitySnapshot> Entities { get; init; }

    [JsonPropertyName("token")]
    public Timestamp? Token { get; init; }
}

public sealed record ComputeEmbeddingsRequest
{
    [JsonPropertyName("entities")]
    public required IReadOnlyList<EntitySnapshot> Entities { get; init; }
}

public sealed record ComputeEmbeddingsResult
{
    [JsonPropertyName("embeddings")]
    public required IReadOnlyList<EntityEmbedding> Embeddings { get; init; }
}

/// <summary>An embedding vector associated with an entity.</summary>
public sealed record EntityEmbedding
{
    [JsonPropertyName("entity-id")]
    public required EntityId EntityId { get; init; }

    [JsonPropertyName("values")]
    public required IReadOnlyList<float> Values { get; init; }
}

public sealed record UpdateEmbeddingsRequest
{
    [JsonPropertyName("updates")]
    public required IReadOnlyList<EmbeddingUpdate> Updates { get; init; }
}

/// <summary>
/// A single embedding update. A <see langword="null"/> <see cref="Values"/> clears any stored
/// embedding for the entity (for example, when the entity has been deleted).
/// </summary>
public sealed record EmbeddingUpdate
{
    [JsonPropertyName("entity-id")]
    public required EntityId EntityId { get; init; }

    [JsonPropertyName("concurrency-tag")]
    public ConcurrencyTag? ConcurrencyTag { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<float>? Values { get; init; }
}

public sealed record UpdateEmbeddingsResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }
}


public readonly record struct EntityId
{
    public EntityId()
    {
        this.Value = Guid.NewGuid();
    }

    public EntityId(Guid value)
    {
        this.Value = value;
    }

    public EntityId(string value)
    {
        this.Value = Guid.Parse(value);
    }

    public Guid Value { get; init; }

    public override string ToString()
    {
        return this.Value.ToString("D");
    }
}

public readonly record struct EntityTypeAndName(
    EntityTypeNameSet TypeNames,
    EntityName EntityName);

[JsonConverter(typeof(EntityNameJsonConverter))]
public readonly struct EntityName : IEquatable<EntityName>
{
    private readonly string[] components;

    public static EntityName Root { get; } = new(Array.Empty<string>());

    public EntityName(params string[] components)
    {
        this.components = components ?? [];
    }

    public string[] Components => this.components ?? [];

    public bool Equals(EntityName other)
    {
        return this.Components.SequenceEqual(other.Components, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is EntityName other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        foreach (var component in this.Components)
        {
            hashCode.Add(component, StringComparer.Ordinal);
        }

        return hashCode.ToHashCode();
    }

    public static bool operator ==(EntityName left, EntityName right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(EntityName left, EntityName right)
    {
        return !left.Equals(right);
    }
}

public readonly record struct EntityReference
{
    public EntityId? EntityId { get; init; }

    public EntityName? EntityName { get; init; }

    public bool IsNameArray { get; init; }
}

public readonly record struct ConcurrencyTag(string Value);

public readonly record struct Timestamp(
    // The date and time when the change was made, in UTC.
    DateTimeOffset DateTime,
    // An orderable specific identifier for the change, e.g. a datetime-prefixed git commit id or a database transaction id,
    // disambiguating changes that were made at the same time.
    string ChangeId);

public readonly record struct RenderTimeIndex(string Value);

public readonly record struct EntityIdTimestamp(
    [property: JsonPropertyName("entity-id")] EntityId EntityId,
    [property: JsonPropertyName("timestamp")] Timestamp Timestamp);

public readonly record struct QueryClauseIdentifier([property: JsonPropertyName("value")] string Value);

public readonly record struct EntityTypeNameSet([property: JsonPropertyName("values")] string[] Values);

public readonly record struct RelationshipTypeNameSet([property: JsonPropertyName("values")] string[] Values);

public readonly record struct RoleNameSet([property: JsonPropertyName("values")] string[] Values);

public readonly struct FieldPath : IEquatable<FieldPath>
{
    [JsonConstructor]
    public FieldPath(params string[] components)
    {
        this.Components = components ?? [];
    }

    [JsonPropertyName("components")]
    public string[] Components { get; }

    public bool Equals(FieldPath other)
    {
        return this.Components.SequenceEqual(other.Components, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is FieldPath other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        foreach (var component in this.Components)
        {
            hashCode.Add(component, StringComparer.Ordinal);
        }

        return hashCode.ToHashCode();
    }

    public static bool operator ==(FieldPath left, FieldPath right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(FieldPath left, FieldPath right)
    {
        return !left.Equals(right);
    }
}

public readonly record struct RegularExpressionPattern([property: JsonPropertyName("value")] string Value);

public readonly record struct MinimumQueryScore([property: JsonPropertyName("value")] double Value);

public sealed record TopLevelQueryClause
{
    [JsonPropertyName("clause-identifier")]
    public required QueryClauseIdentifier ClauseIdentifier { get; init; }

    [JsonPropertyName("clause")]
    public required QueryClause Clause { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "clause-type")]
[JsonDerivedType(typeof(AndQueryClause), "and")]
[JsonDerivedType(typeof(OrQueryClause), "or")]
[JsonDerivedType(typeof(NotQueryClause), "not")]
[JsonDerivedType(typeof(TopQueryClause), "top")]
[JsonDerivedType(typeof(EntityTypeQueryClause), "entity-type")]
[JsonDerivedType(typeof(EntityFieldQueryClause), "entity-field")]
[JsonDerivedType(typeof(EntityVectorQueryClause), "entity-vector")]
[JsonDerivedType(typeof(EntityParticipationQueryClause), "entity-participation")]
[JsonDerivedType(typeof(TransitQueryClause), "transit")]
public abstract record QueryClause;

public sealed record AndQueryClause : QueryClause
{
    [JsonPropertyName("clauses")]
    public required IReadOnlyCollection<QueryClause> Clauses { get; init; }
}

public sealed record OrQueryClause : QueryClause
{
    [JsonPropertyName("clauses")]
    public required IReadOnlyCollection<QueryClause> Clauses { get; init; }
}

public sealed record NotQueryClause : QueryClause
{
    [JsonPropertyName("clause")]
    public required QueryClause Clause { get; init; }
}

public sealed record TopQueryClause : QueryClause
{
    [JsonPropertyName("result-limit")]
    public required QueryResultLimit ResultLimit { get; init; }

    [JsonPropertyName("clause")]
    public required QueryClause Clause { get; init; }
}

public abstract record EntityQueryClause : QueryClause;

public sealed record EntityTypeQueryClause : EntityQueryClause
{
    [JsonPropertyName("entity-type-names")]
    public required EntityTypeNameSet EntityTypeNames { get; init; }
}

public sealed record EntityFieldQueryClause : EntityQueryClause
{
    [JsonPropertyName("field-path")]
    public required FieldPath FieldPath { get; init; }

    [JsonPropertyName("comparison-operator")]
    public required FieldComparisonOperator ComparisonOperator { get; init; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }
}

/// <summary>
/// A semantic (vector) similarity clause. It carries either query text (embedded at query time
/// via the configured embeddings provider) or a precomputed query embedding, and matches entities
/// ranked by cosine similarity, contributing a <see cref="VectorQueryScore"/> per match.
/// </summary>
public sealed record EntityVectorQueryClause : EntityQueryClause
{
    [JsonPropertyName("vector-query-identifier")]
    public required QueryClauseIdentifier VectorQueryIdentifier { get; init; }

    /// <summary>Query text to embed at query time. Mutually exclusive with <see cref="QueryEmbedding"/>.</summary>
    [JsonPropertyName("query-text")]
    public string? QueryText { get; init; }

    /// <summary>A precomputed query embedding. Mutually exclusive with <see cref="QueryText"/>.</summary>
    [JsonPropertyName("query-embedding")]
    public IReadOnlyList<float>? QueryEmbedding { get; init; }

    /// <summary>Maximum number of nearest entities to return (the vector index limit).</summary>
    [JsonPropertyName("number-of-candidates")]
    public int? NumberOfCandidates { get; init; }

    /// <summary>Minimum similarity score for a match to be included.</summary>
    [JsonPropertyName("minimum-query-score")]
    public MinimumQueryScore? MinimumQueryScore { get; init; }
}

public sealed record EntityParticipationQueryClause : EntityQueryClause
{
    [JsonPropertyName("relationship-type-names")]
    public required RelationshipTypeNameSet RelationshipTypeNames { get; init; }

    [JsonPropertyName("participation-role-names")]
    public RoleNameSet? ParticipationRoleNames { get; init; }

    [JsonPropertyName("must-have")]
    public EntityParticipationRequirement? MustHave { get; init; }
}

public sealed record TransitQueryClause : QueryClause
{
    [JsonPropertyName("source-clause-identifier")]
    public required QueryClauseIdentifier SourceClauseIdentifier { get; init; }

    [JsonPropertyName("relationship-type-names")]
    public required RelationshipTypeNameSet RelationshipTypeNames { get; init; }

    [JsonPropertyName("source-participation-role-names")]
    public RoleNameSet? SourceParticipationRoleNames { get; init; }

    [JsonPropertyName("destination-participation-role-names")]
    public RoleNameSet? DestinationParticipationRoleNames { get; init; }

    [JsonPropertyName("match-clause")]
    public required QueryClause MatchClause { get; init; }
}

public sealed record EntityParticipationRequirement
{
    [JsonPropertyName("participation-role-names")]
    public RoleNameSet? ParticipationRoleNames { get; init; }

    [JsonPropertyName("clause")]
    public required QueryClause Clause { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FieldComparisonOperator>))]
public enum FieldComparisonOperator
{
    [JsonStringEnumMemberName("equals")]
    Equals = 0,

    [JsonStringEnumMemberName("greater-than")]
    GreaterThan = 1,

    [JsonStringEnumMemberName("less-than")]
    LessThan = 2,

    [JsonStringEnumMemberName("greater-than-or-equal-to")]
    GreaterThanOrEqualTo = 3,

    [JsonStringEnumMemberName("less-than-or-equal-to")]
    LessThanOrEqualTo = 4,

    [JsonStringEnumMemberName("regular-expression-match")]
    RegularExpressionMatch = 5,

    [JsonStringEnumMemberName("contains")]
    Contains = 6,
}

public readonly record struct QueryResultLimit([property: JsonPropertyName("value")] int Value);

[JsonConverter(typeof(JsonStringEnumConverter<EntityChangeMode>))]
public enum EntityChangeMode
{
    [JsonStringEnumMemberName("replace")]
    Replace = 0,

    [JsonStringEnumMemberName("json-patch")]
    JsonPatch = 1,
}

public enum ConcurrencyMatchState
{
    Matched = 0,
    NotMatched = 1,
}

public enum UpdateState
{
    Added = 0,
    Updated = 1,
    Removed = 2,
    Failed = 3,
}

/// <summary>
/// JSON serialization helpers for converting between JsonElement and DAL value types.
/// </summary>
public static class DataAccessLayerJsonExtensions
{
    /// <summary>
    /// Reads an entity reference value and returns either EntityId or EntityName.
    /// Supports schema entity-reference values: UUID string or entity-name string-array.
    /// </summary>
    public static EntityReference? TryReadEntityReference(this JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var stringValue = element.GetString();
            if (Guid.TryParse(stringValue, out var entityGuid))
            {
                return new EntityReference
                {
                    EntityId = new EntityId(entityGuid),
                };
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var entityName = element.TryReadEntityName();
            if (entityName is not null)
            {
                return new EntityReference
                {
                    EntityName = entityName.Value,
                    IsNameArray = true,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an entity reference by property name from an object JsonElement.
    /// </summary>
    public static EntityReference? TryReadEntityReference(this JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.TryReadEntityReference();
    }

    /// <summary>
    /// Reads an array of strings from a JsonElement and returns an EntityName.
    /// </summary>
    public static EntityName? TryReadEntityName(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var components = new List<string>();
        var hasItems = false;
        foreach (var item in element.EnumerateArray())
        {
            hasItems = true;
            if (item.ValueKind == JsonValueKind.String)
            {
                var component = item.GetString();
                if (!string.IsNullOrWhiteSpace(component))
                {
                    components.Add(component);
                }
            }
        }

        if (!hasItems)
        {
            return EntityName.Root;
        }

        return components.Count > 0 ? new EntityName(components.ToArray()) : null;
    }

    /// <summary>
    /// Reads an array of strings from a JsonElement and returns them as EntityTypeNameSet.
    /// </summary>
    public static EntityTypeNameSet? TryReadEntityTypeNames(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var typeNames = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var typeName = item.GetString();
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    typeNames.Add(typeName);
                }
            }
        }

        return typeNames.Count > 0 ? new EntityTypeNameSet(typeNames.ToArray()) : null;
    }

    /// <summary>
    /// Reads an array of strings from a JsonElement and returns them as RelationshipTypeNameSet.
    /// </summary>
    public static RelationshipTypeNameSet? TryReadRelationshipTypeNames(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var typeNames = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var typeName = item.GetString();
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    typeNames.Add(typeName);
                }
            }
        }

        return typeNames.Count > 0 ? new RelationshipTypeNameSet(typeNames.ToArray()) : null;
    }

    /// <summary>
    /// Reads an array of strings from a JsonElement and returns them as RoleNameSet.
    /// </summary>
    public static RoleNameSet? TryReadRoleNames(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var roleNames = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var roleName = item.GetString();
                if (!string.IsNullOrWhiteSpace(roleName))
                {
                    roleNames.Add(roleName);
                }
            }
        }

        return roleNames.Count > 0 ? new RoleNameSet(roleNames.ToArray()) : null;
    }

    /// <summary>
    /// Extracts an array of strings by property name from a JsonElement.
    /// Returns an empty array if the property doesn't exist or isn't an array.
    /// </summary>
    public static IReadOnlyCollection<string> ExtractStringArray(this JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var propertyElement)
            || propertyElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var item in propertyElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }
}
