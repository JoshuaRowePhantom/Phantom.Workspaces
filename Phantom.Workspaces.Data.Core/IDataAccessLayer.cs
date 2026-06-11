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
}

public sealed record UpdateRequest
{
    public required UpdateMetadata UpdateMetadata { get; init; }

    public required IReadOnlyCollection<EntityChange> Changes { get; init; }
}

public sealed record Markdown
{
    public required string Text { get; init; }
}

public sealed record UpdateMetadata
{
    public required Markdown Comment { get; init; }
}

public sealed record EntityChange
{
    public EntityId? EntityId { get; init; }

    public ConcurrencyTag? ConcurrencyTag { get; init; }

    // null to remove the entity.
    public JsonElement? Data { get; init; }

    public required EntityChangeMode EntityChangeMode { get; init; }
}

public sealed record UpdateResult
{
    public required IReadOnlyCollection<EntityUpdateResult> EntityResults { get; init; }
}

public sealed record EntityUpdateResult
{
    public required UpdateState UpdateState { get; init; }

    public required EntityId RequestedEntityId { get; init; }

    public required EntityId ResultingEntityId { get; init; }

    public ConcurrencyTag? ConcurrencyTag { get; init; }

    public required ConcurrencyMatchState ConcurrencyMatchState { get; init; }

    public EntitySnapshot? CurrentEntity { get; init; }

    public required IReadOnlyCollection<UpdateError> Errors { get; init; }
}

public sealed record UpdateError
{
    public required string Message { get; init; }

    // This is set if there is a related entity id causing the failure.
    public EntityId? RelatedEntityId { get; init; }
}

public sealed record GetRequest
{
    [JsonPropertyName("get-entity")]
    public required IReadOnlyCollection<GetEntityRequest> Entities { get; init; }

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
    public required IReadOnlyCollection<TopLevelQueryClause> Clauses { get; init; }

    public IReadOnlyCollection<Timestamp?>? Timestamps { get; init; }
}

public sealed record QueryResult
{
    public required IReadOnlyCollection<TimestampedQueryBatch> Batches { get; init; }
}

public sealed record GetHistoryRequest
{
    public required IReadOnlyCollection<EntityId> EntityIds { get; init; }
}

public sealed record GetHistoryResult
{
    public required IReadOnlyCollection<EntityHistoryEntry> History { get; init; }
}

/// <summary>
/// Requests a full export of all entities at or after an optional snapshot time.
/// This API is intentionally expensive and should only be used when enumerating everything is unavoidable.
/// </summary>
public sealed record ExportRequest
{
    public Timestamp? SnapshotTime { get; init; }
}

/// <summary>
/// A full export of all entities.
/// This result is intentionally expensive to produce and should only be consumed in rare enumeration scenarios.
/// </summary>
public sealed record ExportResult
{
    public required IReadOnlyCollection<ExportChangeBatch> ChangeBatches { get; init; }

    public required Timestamp FinalSnapshotTime { get; init; }
}

public sealed record GetChangedEntitiesRequest
{
    public required IReadOnlyCollection<EntityIdTimestamp> EntityIdTimestamps { get; init; }
}

public sealed record GetChangedEntitiesResult
{
    public required IReadOnlyCollection<ChangedEntitySnapshot> Entities { get; init; }
}

public sealed record TimestampedEntityBatch
{
    public Timestamp? Timestamp { get; init; }

    public required IReadOnlyCollection<EntitySnapshot> Entities { get; init; }
}

/// <summary>
/// A timestamp-specific batch of query results. 
/// Each batch corresponds to a specific timestamp, and contains the entities that match the query as of that timestamp.
/// </summary>
public sealed record TimestampedQueryBatch
{
    public Timestamp? Timestamp { get; init; }

    public required IReadOnlyCollection<QueryEntitySnapshot> Entities { get; init; }
}

/// <summary>
/// An entity returned by a query.
/// </summary>
/// <param name="MatchingClauseIdentifiers">
/// The set of query clause identifiers that returned this entity.
/// </param>
/// <param name="FullTextQueryScores">
/// The full text match scores for this entity.
/// </param>
public sealed record QueryEntitySnapshot : EntitySnapshot
{
    public Timestamp? ClassifiedTime { get; init; }

    public required IReadOnlyCollection<QueryClauseIdentifier> MatchingClauseIdentifiers { get; init; }

    public required IReadOnlyCollection<FullTextQueryScore> FullTextQueryScores { get; init; }
}

/// <summary>
/// When an entity matches a FullText query, the FullTextQueryScore
/// contains the relevance score for that match, as well as the identifier of the corresponding query clause.
/// </summary>
/// <param name="QueryIdentifier">
/// The identifier of the full text query clause that produced this match score. This can be used to correlate the score with the original query clause, and to distinguish scores from different full text query clauses if an entity matched multiple such clauses.
/// </param>
/// <param name="Score">
/// The relevance score for the match.
/// </param>
public sealed record FullTextQueryScore
{
    public required QueryClauseIdentifier QueryIdentifier { get; init; }

    public required double Score { get; init; }
}

public sealed record EntityHistoryEntry
{
    public required EntityId EntityId { get; init; }

    public required IReadOnlyCollection<Timestamp> UpdateTimes { get; init; }
}

public sealed record ExportChangeBatch
{
    public required Timestamp ChangeTime { get; init; }

    public required IReadOnlyCollection<QueryEntitySnapshot> Entities { get; init; }
}

public sealed record ChangedEntitySnapshot
{
    // The changed entity.
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
    public required EntityId EntityId { get; init; }

    public ConcurrencyTag? ConcurrencyTag { get; init; }

    public required Timestamp ModifiedTime { get; init; }

    public JsonElement? Data { get; init; }

    public required IReadOnlyCollection<EntitySnapshot> Relationships { get; init; }
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
    EntityId EntityId,
    Timestamp Timestamp);

public readonly record struct QueryClauseIdentifier(string Value);

public readonly record struct EntityTypeNameSet(string[] Values);

public readonly record struct RelationshipTypeNameSet(string[] Values);

public readonly record struct RoleNameSet(string[] Values);

public readonly struct FieldPath : IEquatable<FieldPath>
{
    public FieldPath(params string[] components)
    {
        this.Components = components ?? [];
    }

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

public readonly record struct FullTextQueryText(string Value);

public readonly record struct RegularExpressionPattern(string Value);

public readonly record struct MinimumQueryScore(double Value);

public sealed record TopLevelQueryClause
{
    public required QueryClauseIdentifier ClauseIdentifier { get; init; }

    public required QueryClause Clause { get; init; }
}

public abstract record QueryClause;

public sealed record AndQueryClause : QueryClause
{
    public required IReadOnlyCollection<QueryClause> Clauses { get; init; }
}

public sealed record OrQueryClause : QueryClause
{
    public required IReadOnlyCollection<QueryClause> Clauses { get; init; }
}

public sealed record NotQueryClause : QueryClause
{
    public required QueryClause Clause { get; init; }
}

public sealed record TopQueryClause : QueryClause
{
    public required QueryResultLimit ResultLimit { get; init; }

    public required QueryClause Clause { get; init; }
}

public abstract record EntityQueryClause : QueryClause;

public sealed record EntityTypeQueryClause : EntityQueryClause
{
    public required EntityTypeNameSet EntityTypeNames { get; init; }
}

public sealed record EntityFieldQueryClause : EntityQueryClause
{
    public required FieldPath FieldPath { get; init; }

    public required FieldComparisonOperator ComparisonOperator { get; init; }

    public JsonElement? Value { get; init; }
}

public sealed record EntityFullTextQueryClause : EntityQueryClause
{
    public required QueryClauseIdentifier FullTextQueryIdentifier { get; init; }

    public required FullTextQueryText QueryText { get; init; }

    public MinimumQueryScore? MinimumQueryScore { get; init; }
}

public sealed record EntityParticipationQueryClause : EntityQueryClause
{
    public required RelationshipTypeNameSet RelationshipTypeNames { get; init; }

    public RoleNameSet? ParticipationRoleNames { get; init; }

    public EntityParticipationRequirement? MustHave { get; init; }
}

public sealed record TransitQueryClause : QueryClause
{
    public required QueryClauseIdentifier SourceClauseIdentifier { get; init; }

    public required RelationshipTypeNameSet RelationshipTypeNames { get; init; }

    public RoleNameSet? SourceParticipationRoleNames { get; init; }

    public RoleNameSet? DestinationParticipationRoleNames { get; init; }

    public required QueryClause MatchClause { get; init; }
}

public sealed record EntityParticipationRequirement
{
    public RoleNameSet? ParticipationRoleNames { get; init; }

    public required QueryClause Clause { get; init; }
}

public enum FieldComparisonOperator
{
    Equals = 0,
    GreaterThan = 1,
    LessThan = 2,
    GreaterThanOrEqualTo = 3,
    LessThanOrEqualTo = 4,
    RegularExpressionMatch = 5,
    Contains = 6,
}

public readonly record struct QueryResultLimit(int Value);

public enum EntityChangeMode
{
    Replace = 0,
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
