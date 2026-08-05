using System.Globalization;
using System.Text;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A lightweight, embedding-bearing view of a dispatched sub-agent used as a fuzzy-routing
/// candidate. Mirrors the fields of <c>DispatchedSubAgent</c> the router needs, without requiring
/// the router (or its tests) to construct a full dispatched sub-agent with a lease and entity id.
/// </summary>
public sealed record FuzzyRouteCandidate
{
    /// <summary>The sub-agent's exact id.</summary>
    public required string Id { get; init; }

    /// <summary>The sub-agent's human-readable description, used for scoring and disambiguation.</summary>
    public required string Description { get; init; }

    /// <summary>The cached embedding of <see cref="Description"/>.</summary>
    public required IReadOnlyList<float> DescriptionEmbedding { get; init; }

    /// <summary>When the sub-agent last became idle; drives the recency bias.</summary>
    public required DateTimeOffset LastUpdated { get; init; }
}

/// <summary>The outcome of a fuzzy-routing attempt.</summary>
public abstract record FuzzyRouteResult;

/// <summary>A clear winner was found; route to the sub-agent identified by <see cref="Id"/>.</summary>
public sealed record FuzzyRouteMatch(string Id) : FuzzyRouteResult;

/// <summary>
/// Routing was ambiguous (too-close candidates, a stale best match, or no candidates). The dispatcher
/// must emit <see cref="Message"/> and not route.
/// </summary>
public sealed record FuzzyRouteAmbiguous(string Message) : FuzzyRouteResult;

/// <summary>
/// Routes a typed sub-agent identifier that does not exactly match any existing sub-agent to the most
/// similar candidate using cosine similarity, with a recency bias and ambiguity detection. Depends on
/// an <see cref="IEmbeddingsProvider"/> to embed the typed identifier.
/// </summary>
public sealed class SubAgentFuzzyRouter
{
    private const int MaxDisambiguationCandidates = 3;
    private const int MaxDescriptionLength = 60;
    private const string TrailingLine = "Please resubmit with the explicit agent ID.";

    private readonly IEmbeddingsProvider embeddingsProvider;
    private readonly SubAgentDispatcherOptions options;
    private readonly TimeProvider timeProvider;

    public SubAgentFuzzyRouter(
        IEmbeddingsProvider embeddingsProvider,
        SubAgentDispatcherOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(embeddingsProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.embeddingsProvider = embeddingsProvider;
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Attempts to fuzzily route <paramref name="queryId"/> to one of <paramref name="candidates"/>.
    /// </summary>
    public async Task<FuzzyRouteResult> RouteAsync(
        string queryId,
        IReadOnlyList<FuzzyRouteCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryId);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return new FuzzyRouteAmbiguous(BuildDisambiguation(queryId, []));
        }

        var queryEmbeddings = await this.embeddingsProvider.ComputeAsync(
            [new EmbeddingInput { EntityId = new EntityId(Guid.NewGuid()), Text = queryId }],
            cancellationToken).ConfigureAwait(false);
        var queryVector = queryEmbeddings[0].Values;

        var scored = candidates
            .Select(candidate => (Candidate: candidate, Score: CosineSimilarity(queryVector, candidate.DescriptionEmbedding)))
            .OrderByDescending(static entry => entry.Score)
            .ToList();

        var best = scored[0];
        var now = this.timeProvider.GetUtcNow();
        var bestIsStale = now - best.Candidate.LastUpdated > this.options.RecencyThreshold;

        var hasClearMargin = scored.Count == 1
            || best.Score - scored[1].Score >= this.options.AmbiguityThreshold;

        if (hasClearMargin && !bestIsStale)
        {
            return new FuzzyRouteMatch(best.Candidate.Id);
        }

        return new FuzzyRouteAmbiguous(BuildDisambiguation(queryId, scored));
    }

    private static string BuildDisambiguation(
        string queryId,
        IReadOnlyList<(FuzzyRouteCandidate Candidate, double Score)> scored)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Ambiguous sub-agent identifier \"{queryId}\". Matching agents:\n");

        foreach (var (candidate, _) in scored.Take(MaxDisambiguationCandidates))
        {
            var timestamp = candidate.LastUpdated
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
            builder.Append(CultureInfo.InvariantCulture, $"  {candidate.Id} (last updated {timestamp}): \"{Truncate(candidate.Description)}\"\n");
        }

        builder.Append(TrailingLine);
        return builder.ToString();
    }

    private static string Truncate(string description)
    {
        return description.Length > MaxDescriptionLength
            ? description[..MaxDescriptionLength] + "\u2026"
            : description;
    }

    // Mirrors InMemoryQueryEvaluator.CosineSimilarity.
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
