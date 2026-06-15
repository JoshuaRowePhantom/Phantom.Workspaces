using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Data.Vector;

/// <summary>
/// A deterministic, dependency-free embeddings provider that maps text to a fixed-dimension vector
/// using the hashing trick (signed feature hashing over tokens) and L2-normalizes the result.
/// It is not semantically strong, but it is fully deterministic across processes (it uses a stable
/// FNV-1a hash, not the randomized <see cref="string.GetHashCode()"/>), which makes it ideal for
/// tests and local/offline development of the vector-search pipeline.
/// </summary>
public sealed class DeterministicEmbeddingsProvider : IEmbeddingsProvider
{
    public DeterministicEmbeddingsProvider(int dimensions = 256, string modelId = "deterministic-hash-v1")
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        this.Dimensions = dimensions;
        this.ModelId = modelId;
    }

    public int Dimensions { get; }

    public string ModelId { get; }

    public Task<IReadOnlyList<Embedding>> ComputeAsync(
        IReadOnlyList<EmbeddingInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var results = new List<Embedding>(inputs.Count);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new Embedding
            {
                EntityId = input.EntityId,
                Values = this.Embed(input.Text ?? string.Empty),
            });
        }

        return Task.FromResult<IReadOnlyList<Embedding>>(results);
    }

    private float[] Embed(string text)
    {
        var vector = new float[this.Dimensions];
        foreach (var token in Tokenize(text))
        {
            var hash = Fnv1a(token);
            var bucket = (int)(hash % (uint)this.Dimensions);
            var sign = (hash & 0x80000000u) == 0 ? 1f : -1f;
            vector[bucket] += sign;
        }

        double sumOfSquares = 0;
        foreach (var component in vector)
        {
            sumOfSquares += component * (double)component;
        }

        if (sumOfSquares > 0)
        {
            var inverseMagnitude = (float)(1.0 / Math.Sqrt(sumOfSquares));
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] *= inverseMagnitude;
            }
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static uint Fnv1a(string token)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var character in token)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }
}
