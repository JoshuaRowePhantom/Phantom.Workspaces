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
                return this.TakeTop(childIds, topClause.ResultLimit.Value);
            }

            case EntityTypeQueryClause entityTypeClause:
                return this.MatchEntityTypes(entityTypeClause);

            case EntityVectorQueryClause vectorClause:
                return await this.MatchVectorAsync(vectorClause, cancellationToken).ConfigureAwait(false);

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

    private HashSet<EntityId> TakeTop(HashSet<EntityId> ids, int limit)
    {
        if (limit < 0)
        {
            return ids;
        }

        var ranked = ids
            .OrderByDescending(this.BestScore)
            .Take(limit);
        return [.. ranked];
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
