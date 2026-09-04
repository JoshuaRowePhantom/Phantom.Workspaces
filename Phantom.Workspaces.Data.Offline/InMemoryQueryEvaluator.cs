using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data.Vector;

namespace Phantom.Workspaces.Data.Offline;

/// <summary>
/// Evaluates a <see cref="QueryRequest"/>'s clause trees against an in-memory set of candidate
/// entities. Supports the composable clauses (And/Or/Not/Top) plus entity-type and vector
/// (semantic) clauses. Vector clauses contribute per-clause scores. Clauses that require
/// relationship traversal (participation/transit) and field comparisons are not yet supported and
/// raise <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class InMemoryQueryEvaluator
{
    private readonly IReadOnlyList<Candidate> candidates;
    private readonly IEmbeddingsProvider embeddingsProvider;
    private readonly Dictionary<EntityId, List<VectorQueryScore>> vectorScores = [];
    private Dictionary<EntityId, JsonElement?>? dataById;

    public InMemoryQueryEvaluator(
        IReadOnlyList<Candidate> candidates,
        IEmbeddingsProvider embeddingsProvider)
    {
        this.candidates = candidates;
        this.embeddingsProvider = embeddingsProvider;
    }

    /// <summary>A candidate entity (its id and current data) for a single query timestamp.</summary>
    public readonly record struct Candidate(EntityId Id, JsonElement? Data, IReadOnlyList<float>? StoredEmbedding = null);

    /// <summary>An entity that matched at least one top-level clause, with its scores.</summary>
    public sealed record EvaluatedEntity(
        EntityId Id,
        IReadOnlyCollection<QueryClauseIdentifier> MatchingClauseIdentifiers,
        IReadOnlyCollection<VectorQueryScore> VectorScores);

    public async Task<IReadOnlyList<EvaluatedEntity>> EvaluateAsync(
        IReadOnlyCollection<TopLevelQueryClause> clauses,
        CancellationToken cancellationToken)
    {
        var matchedClauses = new Dictionary<EntityId, HashSet<QueryClauseIdentifier>>();
        foreach (var topLevelClause in clauses)
        {
            var matchedIds = await this.EvaluateClauseAsync(topLevelClause.Clause, cancellationToken).ConfigureAwait(false);
            foreach (var id in matchedIds)
            {
                if (!matchedClauses.TryGetValue(id, out var identifiers))
                {
                    matchedClauses[id] = identifiers = [];
                }

                identifiers.Add(topLevelClause.ClauseIdentifier);
            }
        }

        return matchedClauses
            .Select(entry => new EvaluatedEntity(
                entry.Key,
                entry.Value.ToArray(),
                this.vectorScores.TryGetValue(entry.Key, out var vector) ? vector.ToArray() : []))
            .ToArray();
    }

    private async Task<HashSet<EntityId>> EvaluateClauseAsync(QueryClause clause, CancellationToken cancellationToken)
    {
        switch (clause)
        {
            case AndQueryClause andClause:
            {
                HashSet<EntityId>? result = null;
                foreach (var child in andClause.Clauses)
                {
                    var childIds = await this.EvaluateClauseAsync(child, cancellationToken).ConfigureAwait(false);
                    if (result is null)
                    {
                        result = childIds;
                    }
                    else
                    {
                        result.IntersectWith(childIds);
                    }

                    if (result.Count == 0)
                    {
                        break;
                    }
                }

                return result ?? this.AllIds();
            }

            case OrQueryClause orClause:
            {
                var result = new HashSet<EntityId>();
                foreach (var child in orClause.Clauses)
                {
                    result.UnionWith(await this.EvaluateClauseAsync(child, cancellationToken).ConfigureAwait(false));
                }

                return result;
            }

            case NotQueryClause notClause:
            {
                var inner = await this.EvaluateClauseAsync(notClause.Clause, cancellationToken).ConfigureAwait(false);
                var result = this.AllIds();
                result.ExceptWith(inner);
                return result;
            }

            case TopQueryClause topClause:
            {
                var childIds = await this.EvaluateClauseAsync(topClause.Clause, cancellationToken).ConfigureAwait(false);
                return this.TakeTop(childIds, topClause.ResultLimit.Value, topClause.SortSpecifications);
            }

            case EntityTypeQueryClause entityTypeClause:
                return this.MatchEntityTypes(entityTypeClause);

            case EntityFieldQueryClause fieldClause:
                return this.MatchField(fieldClause);

            case EntityVectorQueryClause vectorClause:
                return await this.MatchVectorAsync(vectorClause, cancellationToken).ConfigureAwait(false);

            case EntityParticipationQueryClause participationClause:
                return await this.MatchParticipationAsync(participationClause, cancellationToken).ConfigureAwait(false);

            case TransitQueryClause transitClause:
                return await this.MatchTransitAsync(transitClause, cancellationToken).ConfigureAwait(false);

            default:
                throw new NotSupportedException(
                    $"In-memory query evaluation does not support the '{clause.GetType().Name}' clause.");
        }
    }

    private HashSet<EntityId> AllIds() => [.. this.candidates.Select(static candidate => candidate.Id)];

    private HashSet<EntityId> MatchEntityTypes(EntityTypeQueryClause clause)
    {
        var requiredTypes = clause.EntityTypeNames.Values;
        var result = new HashSet<EntityId>();
        foreach (var candidate in this.candidates)
        {
            var types = ReadEntityTypes(candidate.Data);
            if (requiredTypes.All(types.Contains))
            {
                result.Add(candidate.Id);
            }
        }

        return result;
    }

    private HashSet<EntityId> MatchField(EntityFieldQueryClause clause)
    {
        var result = new HashSet<EntityId>();
        foreach (var candidate in this.candidates)
        {
            if (candidate.Data is not { } data)
            {
                continue;
            }

            if (NavigateField(data, clause.FieldPath.Components) is { } fieldValue
                && CompareField(fieldValue, clause.ComparisonOperator, clause.Value))
            {
                result.Add(candidate.Id);
            }
        }

        return result;
    }

    private static JsonElement? NavigateField(JsonElement root, IReadOnlyList<string> components)
    {
        var current = root;
        foreach (var component in components)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(component, out var next))
                {
                    return null;
                }

                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(component, out var index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    private static bool CompareField(JsonElement fieldValue, FieldComparisonOperator comparisonOperator, JsonElement? clauseValue)
    {
        switch (comparisonOperator)
        {
            case FieldComparisonOperator.Equals:
                return clauseValue is { } equalsValue && JsonElement.DeepEquals(fieldValue, equalsValue);

            case FieldComparisonOperator.Contains:
                return ContainsMatch(fieldValue, clauseValue);

            case FieldComparisonOperator.RegularExpressionMatch:
                return clauseValue is { ValueKind: JsonValueKind.String } pattern
                    && fieldValue.ValueKind == JsonValueKind.String
                    && System.Text.RegularExpressions.Regex.IsMatch(
                        fieldValue.GetString() ?? string.Empty,
                        pattern.GetString() ?? string.Empty);

            case FieldComparisonOperator.GreaterThan:
            case FieldComparisonOperator.LessThan:
            case FieldComparisonOperator.GreaterThanOrEqualTo:
            case FieldComparisonOperator.LessThanOrEqualTo:
                return clauseValue is { } orderedValue && CompareOrdered(fieldValue, orderedValue, comparisonOperator);

            default:
                return false;
        }
    }

    private static bool ContainsMatch(JsonElement fieldValue, JsonElement? clauseValue)
    {
        if (clauseValue is not { } value)
        {
            return false;
        }

        if (fieldValue.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String)
        {
            return (fieldValue.GetString() ?? string.Empty).Contains(value.GetString() ?? string.Empty, StringComparison.Ordinal);
        }

        if (fieldValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in fieldValue.EnumerateArray())
            {
                if (JsonElement.DeepEquals(element, value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CompareOrdered(JsonElement fieldValue, JsonElement clauseValue, FieldComparisonOperator comparisonOperator)
    {
        int comparison;
        if (fieldValue.ValueKind == JsonValueKind.Number && clauseValue.ValueKind == JsonValueKind.Number)
        {
            comparison = fieldValue.GetDouble().CompareTo(clauseValue.GetDouble());
        }
        else if (fieldValue.ValueKind == JsonValueKind.String && clauseValue.ValueKind == JsonValueKind.String)
        {
            comparison = string.CompareOrdinal(fieldValue.GetString(), clauseValue.GetString());
        }
        else
        {
            return false;
        }

        return comparisonOperator switch
        {
            FieldComparisonOperator.GreaterThan => comparison > 0,
            FieldComparisonOperator.LessThan => comparison < 0,
            FieldComparisonOperator.GreaterThanOrEqualTo => comparison >= 0,
            FieldComparisonOperator.LessThanOrEqualTo => comparison <= 0,
            _ => false,
        };
    }

    private async Task<HashSet<EntityId>> MatchParticipationAsync(
        EntityParticipationQueryClause clause,
        CancellationToken cancellationToken)
    {
        var relationshipTypeNames = clause.RelationshipTypeNames.Values is { Length: > 0 } typeValues
            ? new HashSet<string>(typeValues, StringComparer.Ordinal)
            : null;
        var resultRoleNames = clause.ParticipationRoleNames?.Values is { Length: > 0 } roleValues
            ? new HashSet<string>(roleValues, StringComparer.Ordinal)
            : null;

        HashSet<EntityId>? mustHaveMatches = null;
        HashSet<string>? mustHaveRoleNames = null;
        if (clause.MustHave is { } mustHave)
        {
            mustHaveMatches = await this.EvaluateClauseAsync(mustHave.Clause, cancellationToken).ConfigureAwait(false);
            mustHaveRoleNames = mustHave.ParticipationRoleNames?.Values is { Length: > 0 } mustHaveRoles
                ? new HashSet<string>(mustHaveRoles, StringComparer.Ordinal)
                : null;
        }

        var result = new HashSet<EntityId>();
        foreach (var candidate in this.candidates)
        {
            if (candidate.Data is not { } data)
            {
                continue;
            }

            // Only relationships of the requested type(s) participate.
            if (relationshipTypeNames is not null && !relationshipTypeNames.Overlaps(ReadEntityTypes(data)))
            {
                continue;
            }

            if (!data.TryGetProperty("participants", out var participants)
                || participants.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // MustHave: the relationship must also carry a participant (in the given roles, or any
            // role when unspecified) that matches the sub-clause - e.g. the 'user' participant is the
            // current user.
            if (clause.MustHave is not null && !MustHaveSatisfied(participants, mustHaveRoleNames, mustHaveMatches!))
            {
                continue;
            }

            // Collect entities participating in the requested roles (or all roles when unspecified).
            foreach (var participant in participants.EnumerateObject())
            {
                if (resultRoleNames is not null && !resultRoleNames.Contains(participant.Name))
                {
                    continue;
                }

                foreach (var id in ReadParticipantIds(participant.Value))
                {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    private async Task<HashSet<EntityId>> MatchTransitAsync(TransitQueryClause clause, CancellationToken cancellationToken)
    {
        // Find the source entities that match the MatchClause.
        var sourceMatches = await this.EvaluateClauseAsync(clause.MatchClause, cancellationToken).ConfigureAwait(false);

        var relationshipTypeNames = clause.RelationshipTypeNames.Values is { Length: > 0 } typeNames
            ? new HashSet<string>(typeNames, StringComparer.Ordinal)
            : null;
        var sourceRoleNames = clause.SourceParticipationRoleNames?.Values is { Length: > 0 } sourceRoles
            ? new HashSet<string>(sourceRoles, StringComparer.Ordinal)
            : null;
        var destRoleNames = clause.DestinationParticipationRoleNames?.Values is { Length: > 0 } destRoles
            ? new HashSet<string>(destRoles, StringComparer.Ordinal)
            : null;

        var result = new HashSet<EntityId>();
        foreach (var candidate in this.candidates)
        {
            if (candidate.Data is not { } data)
            {
                continue;
            }

            // Only relationships of the requested type(s) participate.
            if (relationshipTypeNames is not null && !relationshipTypeNames.Overlaps(ReadEntityTypes(data)))
            {
                continue;
            }

            if (!data.TryGetProperty("participants", out var participants)
                || participants.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Check if this relationship has a source participant that matched the source clause.
            var hasSourceMatch = false;
            foreach (var participant in participants.EnumerateObject())
            {
                if (sourceRoleNames is not null && !sourceRoleNames.Contains(participant.Name))
                {
                    continue;
                }

                foreach (var id in ReadParticipantIds(participant.Value))
                {
                    if (sourceMatches.Contains(id))
                    {
                        hasSourceMatch = true;
                        break;
                    }
                }

                if (hasSourceMatch)
                {
                    break;
                }
            }

            if (!hasSourceMatch)
            {
                continue;
            }

            // Collect destination participants from this relationship.
            foreach (var participant in participants.EnumerateObject())
            {
                if (destRoleNames is not null && !destRoleNames.Contains(participant.Name))
                {
                    continue;
                }

                foreach (var id in ReadParticipantIds(participant.Value))
                {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    private static bool MustHaveSatisfied(
        JsonElement participants,
        HashSet<string>? mustHaveRoleNames,
        HashSet<EntityId> mustHaveMatches)
    {
        foreach (var participant in participants.EnumerateObject())
        {
            if (mustHaveRoleNames is not null && !mustHaveRoleNames.Contains(participant.Name))
            {
                continue;
            }

            foreach (var id in ReadParticipantIds(participant.Value))
            {
                if (mustHaveMatches.Contains(id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<EntityId> ReadParticipantIds(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (TryParseEntityId(value, out var id))
            {
                yield return id;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                if (TryParseEntityId(element, out var id))
                {
                    yield return id;
                }
            }
        }
    }

    private static bool TryParseEntityId(JsonElement element, out EntityId id)
    {
        id = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var guid))
        {
            return false;
        }

        id = new EntityId(guid);
        return true;
    }

    private async Task<HashSet<EntityId>> MatchVectorAsync(EntityVectorQueryClause clause, CancellationToken cancellationToken)
    {
        var queryVector = await this.ResolveQueryVectorAsync(clause, cancellationToken).ConfigureAwait(false);

        // Candidates with a stored embedding (set via UpdateEmbeddings) use it directly; others have
        // their embedding computed on the fly from their text projection.
        var candidateEmbeddings = new List<(EntityId Id, IReadOnlyList<float> Values)>();
        var toCompute = new List<EmbeddingInput>();
        foreach (var candidate in this.candidates)
        {
            // Deleted entities (null data) carry no searchable content, so they are never vector
            // matches - consistent with entity-type clauses and the MongoDB is-deleted filter.
            if (candidate.Data is null)
            {
                continue;
            }

            if (candidate.StoredEmbedding is { Count: > 0 } stored)
            {
                candidateEmbeddings.Add((candidate.Id, stored));
            }
            else
            {
                toCompute.Add(new EmbeddingInput
                {
                    EntityId = candidate.Id,
                    Text = EntityTextProjection.ProjectText(candidate.Data),
                });
            }
        }

        if (toCompute.Count > 0)
        {
            var computed = await this.embeddingsProvider.ComputeAsync(toCompute, cancellationToken).ConfigureAwait(false);
            foreach (var embedding in computed)
            {
                candidateEmbeddings.Add((embedding.EntityId, embedding.Values));
            }
        }

        var minimumScore = clause.MinimumQueryScore?.Value ?? double.NegativeInfinity;
        var scored = new List<(EntityId Id, double Score)>();
        foreach (var (id, values) in candidateEmbeddings)
        {
            var score = CosineSimilarity(queryVector, values);
            if (score >= minimumScore)
            {
                scored.Add((id, score));
            }
        }

        scored.Sort(static (left, right) => right.Score.CompareTo(left.Score));

        IEnumerable<(EntityId Id, double Score)> selected = scored;
        if (clause.NumberOfCandidates is { } limit && limit >= 0)
        {
            selected = scored.Take(limit);
        }

        var result = new HashSet<EntityId>();
        foreach (var (id, score) in selected)
        {
            result.Add(id);
            this.AddVectorScore(id, clause.VectorQueryIdentifier, score);
        }

        return result;
    }

    private async Task<IReadOnlyList<float>> ResolveQueryVectorAsync(
        EntityVectorQueryClause clause,
        CancellationToken cancellationToken)
    {
        if (clause.QueryEmbedding is { Count: > 0 })
        {
            return clause.QueryEmbedding;
        }

        if (!string.IsNullOrWhiteSpace(clause.QueryText))
        {
            var queryEmbeddings = await this.embeddingsProvider.ComputeAsync(
                [new EmbeddingInput { EntityId = default, Text = clause.QueryText! }],
                cancellationToken).ConfigureAwait(false);
            return queryEmbeddings[0].Values;
        }

        throw new ArgumentException("A vector query clause requires query-text or a query-embedding.");
    }

    private HashSet<EntityId> TakeTop(HashSet<EntityId> ids, int limit, IReadOnlyList<SortSpecification>? sorts)
    {
        if (limit < 0)
        {
            return ids;
        }

        // When explicit sort specifications are supplied, order matched entities by those fields
        // (compound, in list order) BEFORE taking the top-N, so the limit is a true top-N-by-order
        // rather than an arbitrary subset. Vector-score ordering is retained only for unsorted
        // (e.g. vector) queries.
        var ranked = sorts is { Count: > 0 }
            ? this.OrderBySpecifications(ids, sorts).Take(limit)
            : ids.OrderByDescending(this.BestScore).Take(limit);
        return [.. ranked];
    }

    private IOrderedEnumerable<EntityId> OrderBySpecifications(
        HashSet<EntityId> ids,
        IReadOnlyList<SortSpecification> sorts)
    {
        var lookup = this.DataById();
        IOrderedEnumerable<EntityId>? ordered = null;
        foreach (var specification in sorts)
        {
            var components = specification.FieldPath.Components ?? [];
            JsonElement? KeySelector(EntityId id) =>
                lookup.TryGetValue(id, out var data) && data is { } value
                    ? NavigateField(value, components)
                    : null;

            var descending = specification.Direction == SortDirection.Descending;
            if (ordered is null)
            {
                ordered = descending
                    ? ids.OrderByDescending(KeySelector, FieldValueComparer.Instance)
                    : ids.OrderBy(KeySelector, FieldValueComparer.Instance);
            }
            else
            {
                ordered = descending
                    ? ordered.ThenByDescending(KeySelector, FieldValueComparer.Instance)
                    : ordered.ThenBy(KeySelector, FieldValueComparer.Instance);
            }
        }

        return ordered ?? ids.OrderBy(static id => id.Value);
    }

    private Dictionary<EntityId, JsonElement?> DataById()
    {
        if (this.dataById is null)
        {
            var map = new Dictionary<EntityId, JsonElement?>();
            foreach (var candidate in this.candidates)
            {
                map[candidate.Id] = candidate.Data;
            }

            this.dataById = map;
        }

        return this.dataById;
    }

    /// <summary>
    /// Orders field values monotonically: numbers numerically, strings ordinally, and missing values
    /// (absent field) before any present value. Incomparable kinds compare equal (stable order).
    /// </summary>
    private sealed class FieldValueComparer : IComparer<JsonElement?>
    {
        public static readonly FieldValueComparer Instance = new();

        public int Compare(JsonElement? x, JsonElement? y)
        {
            if (x is not { } left)
            {
                return y is null ? 0 : -1;
            }

            if (y is not { } right)
            {
                return 1;
            }

            if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            {
                return left.GetDouble().CompareTo(right.GetDouble());
            }

            if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            {
                return string.CompareOrdinal(left.GetString(), right.GetString());
            }

            return 0;
        }
    }

    private double BestScore(EntityId id)
    {
        var best = double.NegativeInfinity;
        if (this.vectorScores.TryGetValue(id, out var vectorScoreList))
        {
            foreach (var score in vectorScoreList)
            {
                best = Math.Max(best, score.Score);
            }
        }

        return best;
    }

    private void AddVectorScore(EntityId id, QueryClauseIdentifier identifier, double score)
    {
        if (!this.vectorScores.TryGetValue(id, out var list))
        {
            this.vectorScores[id] = list = [];
        }

        list.Add(new VectorQueryScore { QueryIdentifier = identifier, Score = score });
    }

    private static IReadOnlySet<string> ReadEntityTypes(JsonElement? data)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (data is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("entity-types", out var entityTypes)
            && entityTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entityTypes.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        types.Add(value);
                    }
                }
            }
        }

        return types;
    }

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * (double)right[index];
            leftMagnitude += left[index] * (double)left[index];
            rightMagnitude += right[index] * (double)right[index];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }
}
