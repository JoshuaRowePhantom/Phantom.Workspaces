using System.Text.Json;

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
    public required IReadOnlyCollection<GetEntityRequest> Entities { get; init; }

    // null means do not return relationships, empty means return all, non-empty means return matching relationships.
    public IReadOnlyCollection<GetRelationshipRequest>? RelationshipsToReturn { get; init; }

    public IReadOnlyCollection<Timestamp?>? Timestamps { get; init; }
}

public sealed record GetEntityRequest
{
    public EntityId? EntityId { get; init; }

    public EntityName? EntityName { get; init; }

    public EntityTypeNames? EntityTypeNames { get; init; }

    // null means inherit request-level value; empty means return all; non-empty means return matching relationships.
    public IReadOnlyCollection<GetRelationshipRequest>? RelationshipsToReturn { get; init; }
}

public sealed record GetRelationshipRequest
{
    public RelationshipTypeNames? RelationshipTypeNames { get; init; }

    public RoleNames? RelationshipRoleNames { get; init; }
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

public readonly record struct EntityId(Guid Value);

public readonly record struct EntityTypeAndName(
    EntityTypeNames TypeNames,
    EntityName EntityName);

public readonly record struct EntityName(
    string Components);

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

public readonly record struct EntityTypeNames(string[] Values);

public readonly record struct RelationshipTypeNames(string[] Values);

public readonly record struct RoleNames(string[] Values);

public readonly record struct FieldPath(string[] Components);

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
    public required EntityTypeNames EntityTypeNames { get; init; }
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
    public required RelationshipTypeNames RelationshipTypeNames { get; init; }

    public RoleNames? ParticipationRoleNames { get; init; }

    public EntityParticipationRequirement? MustHave { get; init; }
}

public sealed record TransitQueryClause : QueryClause
{
    public required QueryClauseIdentifier SourceClauseIdentifier { get; init; }

    public required RelationshipTypeNames RelationshipTypeNames { get; init; }

    public RoleNames? SourceParticipationRoleNames { get; init; }

    public RoleNames? DestinationParticipationRoleNames { get; init; }

    public required QueryClause MatchClause { get; init; }
}

public sealed record EntityParticipationRequirement
{
    public RoleNames? ParticipationRoleNames { get; init; }

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
