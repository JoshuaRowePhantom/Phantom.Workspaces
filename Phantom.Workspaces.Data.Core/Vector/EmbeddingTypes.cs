using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Data.Vector;

/// <summary>
/// A normalized, text-only projection of an entity used as the input to embedding computation.
/// Binary / non-text (MIME) content is stripped before this point - see
/// <see cref="EntityTextProjection"/>.
/// </summary>
public sealed record EmbeddingInput
{
    /// <summary>The entity the embedding is computed for.</summary>
    public required EntityId EntityId { get; init; }

    /// <summary>The normalized text the embedding is computed from.</summary>
    public required string Text { get; init; }
}

/// <summary>A computed embedding vector for an entity.</summary>
public sealed record Embedding
{
    /// <summary>The entity the embedding belongs to.</summary>
    public required EntityId EntityId { get; init; }

    /// <summary>The embedding vector.</summary>
    public required IReadOnlyList<float> Values { get; init; }
}

/// <summary>
/// Abstracts embedding computation so providers (deterministic/local, OpenAI, etc.) can be
/// swapped. The <see cref="ModelId"/> and <see cref="Dimensions"/> are recorded with stored
/// vectors so a model change can trigger reindexing and so queries use a matching query embedding.
/// </summary>
public interface IEmbeddingsProvider
{
    /// <summary>The dimensionality of vectors this provider produces.</summary>
    int Dimensions { get; }

    /// <summary>An identifier for the embedding model, stored alongside vectors.</summary>
    string ModelId { get; }

    /// <summary>Computes embeddings for the supplied inputs, preserving input order.</summary>
    Task<IReadOnlyList<Embedding>> ComputeAsync(
        IReadOnlyList<EmbeddingInput> inputs,
        CancellationToken cancellationToken = default);
}
