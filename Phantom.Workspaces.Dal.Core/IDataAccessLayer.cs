using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Dal.Core;

public interface IDataAccessLayer
{
    Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default);

    Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);

    Task<RenderResult> RenderAsync(
        RenderRequest request,
        CancellationToken cancellationToken = default);

    Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record UpdateRequest(
    IReadOnlyCollection<EntityChange> Changes);

public sealed record EntityChange(
    EntityId? EntityId,
    ConcurrencyTag? ConcurrencyTag,
    JsonNode? Data,
    EntityChangeKind ChangeKind,
    MergeMode MergeMode);

public sealed record UpdateResult(
    IReadOnlyCollection<EntityUpdateResult> EntityResults);

public sealed record EntityUpdateResult(
    EntityId RequestedEntityId,
    EntityId ResultingEntityId,
    ConcurrencyTag? ConcurrencyTag,
    ConcurrencyMatchState ConcurrencyMatchState,
    RemovalState RemovalState);

public sealed record GetRequest(
    IReadOnlyCollection<EntityId> EntityIds,
    IReadOnlyCollection<Timestamp?> Timestamps);

public sealed record QueryRequest(
    IReadOnlyCollection<TopLevelQueryClause> Clauses,
    IReadOnlyCollection<Timestamp?> Timestamps);

public sealed record RenderRequest(
    EntityId ViewEntityId,
    RenderTimeIndex? TimeIndex);

public sealed record GetHistoryRequest(
    IReadOnlyCollection<EntityId> EntityIds);

public sealed record ExportRequest(
    Timestamp? SnapshotTime);

public sealed record GetResult(
    IReadOnlyCollection<TimestampedEntityBatch> Batches);

public sealed record QueryResult(
    IReadOnlyCollection<TimestampedQueryBatch> Batches);

public sealed record RenderResult(
    IReadOnlyCollection<EntitySnapshot> Entities,
    RenderTimeIndex? TimeIndex);

public sealed record GetHistoryResult(
    IReadOnlyCollection<EntityHistoryEntry> History);

public sealed record ExportResult(
    IReadOnlyCollection<ExportChangeBatch> ChangeBatches,
    Timestamp FinalSnapshotTime);

public sealed record TimestampedEntityBatch(
    Timestamp? Timestamp,
    IReadOnlyCollection<EntitySnapshot> Entities);

public sealed record TimestampedQueryBatch(
    Timestamp? Timestamp,
    IReadOnlyCollection<QueryEntitySnapshot> Entities);

public sealed record QueryEntitySnapshot(
    EntityId EntityId,
    ConcurrencyTag? ConcurrencyTag,
    Timestamp ModifiedTime,
    Timestamp? ClassifiedTime,
    JsonNode? Data,
    IReadOnlyCollection<QueryClauseIdentifier> MatchingClauseIdentifiers,
    IReadOnlyCollection<FullTextQueryScore> FullTextQueryScores)
    : EntitySnapshot(
        EntityId,
        ConcurrencyTag,
        ModifiedTime,
        ClassifiedTime,
        Data);

public sealed record FullTextQueryScore(
    QueryClauseIdentifier QueryIdentifier,
    double Score);

public sealed record EntityHistoryEntry(
    EntityId EntityId,
    IReadOnlyCollection<Timestamp> UpdateTimes);

public sealed record ExportChangeBatch(
    Timestamp ChangeTime,
    IReadOnlyCollection<QueryEntitySnapshot> Entities);

public record EntitySnapshot(
    EntityId EntityId,
    ConcurrencyTag? ConcurrencyTag,
    Timestamp ModifiedTime,
    Timestamp? ClassifiedTime,
    JsonNode? Data);

public readonly record struct EntityId(Guid Value);

public readonly record struct ConcurrencyTag(string Value);

public readonly record struct Timestamp(
    // The date and time when the change was made, in UTC.
    DateTimeOffset Value, 
    // A specific identifier for the change, e.g. a git commit id or a database transaction id,
    // or a concatenation of a date time and git commit id.
    string ValueString);

public readonly record struct RenderTimeIndex(string Value);

public readonly record struct QueryClauseIdentifier(string Value);

public readonly record struct EntityTypeName(string Value);

public readonly record struct RelationshipTypeName(string Value);

public readonly record struct RoleName(string Value);

public readonly record struct FieldPath(string Value);

public readonly record struct QueryText(string Value);

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

public sealed record EntityFieldQueryClause(
    FieldPath FieldPath,
    FieldComparisonOperator ComparisonOperator,
    JsonNode? Value) : EntityQueryClause;

public sealed record EntityFullTextQueryClause(
    QueryClauseIdentifier FullTextQueryIdentifier,
    QueryText QueryText,
    MinimumQueryScore? MinimumQueryScore) : EntityQueryClause;

public sealed record EntityParticipationQueryClause(
    RelationshipTypeName RelationshipTypeName,
    RoleName? ParticipationRoleName,
    EntityParticipationRequirement? MustHave) : EntityQueryClause;

public sealed record TransitQueryClause(
    QueryClauseIdentifier SourceClauseIdentifier,
    RelationshipTypeName RelationshipTypeName,
    RoleName? SourceParticipationRoleName,
    RoleName? DestinationParticipationRoleName,
    QueryClause MatchClause) : QueryClause;

public sealed record EntityParticipationRequirement(
    RoleName? ParticipationRoleName,
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

public enum EntityChangeKind
{
    Upsert = 0,
    Remove = 1,
}

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

public enum RemovalState
{
    Retained = 0,
    Removed = 1,
}
