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

public sealed record UpdateRequest(
    UpdateMetadata UpdateMetadata,
    IReadOnlyCollection<EntityChange> Changes);

public sealed record Markdown(
    string Text);

public sealed record UpdateMetadata(
    Markdown Comment);

public sealed record EntityChange(
    EntityId? EntityId,
    ConcurrencyTag? ConcurrencyTag,
    // null to remove the entity,
    JsonElement? Data,
    MergeMode MergeMode);

public sealed record UpdateResult(
    IReadOnlyCollection<EntityUpdateResult> EntityResults);

public sealed record EntityUpdateResult(
    UpdateState UpdateState,
    EntityId RequestedEntityId,
    EntityId ResultingEntityId,
    ConcurrencyTag? ConcurrencyTag,
    ConcurrencyMatchState ConcurrencyMatchState,
    EntitySnapshot? CurrentEntity,
    IReadOnlyCollection<UpdateError> Errors);

public sealed record UpdateError(
    string Message,
    // This is set if there is a related entity id causing the failure.
    EntityId? RelatedEntityId);

public sealed record GetRequest(
    IReadOnlyCollection<EntityId>? EntityIds,
    IReadOnlyCollection<EntityName>? EntityNames,
    IReadOnlyCollection<EntityTypeAndName>? EntityTypeAndNames,
    IReadOnlyCollection<Timestamp?>? Timestamps);

public sealed record GetResult(
    IReadOnlyCollection<TimestampedEntityBatch> Batches);

/// <summary>
/// A request to query for entities. 
/// </summary>
/// <param name="Clauses">
/// The set of clauses to query for. Each clause can produce a set of entities.
/// </param>
/// <param name="Timestamps">
/// The set of timestamps to query as-of. null means "now".
/// </param>
public sealed record QueryRequest(
    IReadOnlyCollection<TopLevelQueryClause> Clauses,
    IReadOnlyCollection<Timestamp?>? Timestamps);

public sealed record QueryResult(
    IReadOnlyCollection<TimestampedQueryBatch> Batches);

public sealed record GetHistoryRequest(
    IReadOnlyCollection<EntityId> EntityIds);

public sealed record GetHistoryResult(
    IReadOnlyCollection<EntityHistoryEntry> History);

/// <summary>
/// Requests a full export of all entities at or after an optional snapshot time.
/// This API is intentionally expensive and should only be used when enumerating everything is unavoidable.
/// </summary>
public sealed record ExportRequest(
    Timestamp? SnapshotTime);

/// <summary>
/// A full export of all entities.
/// This result is intentionally expensive to produce and should only be consumed in rare enumeration scenarios.
/// </summary>
public sealed record ExportResult(
    IReadOnlyCollection<ExportChangeBatch> ChangeBatches,
    Timestamp FinalSnapshotTime);

public sealed record GetChangedEntitiesRequest(
    IReadOnlyCollection<EntityIdTimestamp> EntityIdTimestamps);

public sealed record GetChangedEntitiesResult(
    IReadOnlyCollection<ChangedEntitySnapshot> Entities);

public sealed record TimestampedEntityBatch(
    Timestamp? Timestamp,
    IReadOnlyCollection<EntitySnapshot> Entities);

/// <summary>
/// A timestamp-specific batch of query results. 
/// Each batch corresponds to a specific timestamp, and contains the entities that match the query as of that timestamp.
/// </summary>
public sealed record TimestampedQueryBatch(
    Timestamp? Timestamp,
    IReadOnlyCollection<QueryEntitySnapshot> Entities);

/// <summary>
/// An entity returned by a query.
/// </summary>
/// <param name="MatchingClauseIdentifiers">
/// The set of query clause identifiers that returned this entity.
/// </param>
/// <param name="FullTextQueryScores">
/// The full text match scores for this entity.
/// </param>
public sealed record QueryEntitySnapshot(
    EntityId EntityId,
    ConcurrencyTag? ConcurrencyTag,
    Timestamp ModifiedTime,
    Timestamp? ClassifiedTime,
    JsonElement? Data,
    IReadOnlyCollection<QueryClauseIdentifier> MatchingClauseIdentifiers,
    IReadOnlyCollection<FullTextQueryScore> FullTextQueryScores)
    : EntitySnapshot(
        EntityId,
        ConcurrencyTag,
        ModifiedTime,
        Data);

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
public sealed record FullTextQueryScore(
    QueryClauseIdentifier QueryIdentifier,
    double Score);

public sealed record EntityHistoryEntry(
    EntityId EntityId,
    IReadOnlyCollection<Timestamp> UpdateTimes);

public sealed record ExportChangeBatch(
    Timestamp ChangeTime,
    IReadOnlyCollection<QueryEntitySnapshot> Entities);

public sealed record ChangedEntitySnapshot(
    // The changed entity.
    EntitySnapshot? Entity);

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
public record EntitySnapshot(
    EntityId EntityId,
    ConcurrencyTag? ConcurrencyTag,
    Timestamp ModifiedTime,
    JsonElement? Data);

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

public sealed record TopLevelQueryClause(
    QueryClauseIdentifier ClauseIdentifier,
    QueryClause Clause);

public abstract record QueryClause;

public sealed record AndQueryClause(
    IReadOnlyCollection<QueryClause> Clauses) : QueryClause;

public sealed record OrQueryClause(
    IReadOnlyCollection<QueryClause> Clauses) : QueryClause;

public sealed record NotQueryClause(
    QueryClause Clause) : QueryClause;

public sealed record TopQueryClause(
    QueryResultLimit ResultLimit,
    QueryClause Clause) : QueryClause;

public abstract record EntityQueryClause : QueryClause;

public sealed record EntityTypeQueryClause(
    EntityTypeNames EntityTypeNames) : EntityQueryClause;

public sealed record EntityFieldQueryClause(
    FieldPath FieldPath,
    FieldComparisonOperator ComparisonOperator,
    JsonElement? Value) : EntityQueryClause;

public sealed record EntityFullTextQueryClause(
    QueryClauseIdentifier FullTextQueryIdentifier,
    FullTextQueryText QueryText,
    MinimumQueryScore? MinimumQueryScore) : EntityQueryClause;

public sealed record EntityParticipationQueryClause(
    RelationshipTypeNames RelationshipTypeNames,
    RoleNames? ParticipationRoleNames,
    EntityParticipationRequirement? MustHave) : EntityQueryClause;

public sealed record TransitQueryClause(
    QueryClauseIdentifier SourceClauseIdentifier,
    RelationshipTypeNames RelationshipTypeNames,
    RoleNames? SourceParticipationRoleNames,
    RoleNames? DestinationParticipationRoleNames,
    QueryClause MatchClause) : QueryClause;

public sealed record EntityParticipationRequirement(
    RoleNames? ParticipationRoleNames,
    QueryClause Clause);

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

public enum MergeMode
{
    Merge = 0,
    Replace = 1,
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
