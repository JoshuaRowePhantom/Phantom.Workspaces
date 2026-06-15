using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Xunit;

namespace Phantom.Workspaces.Data.Core.Tests;

public sealed class EntityTextProjectionTests
{
    [Fact]
    public void ProjectText_CollectsStringLeaves_InDocumentOrder()
    {
        var data = Parse(
            """
            {
              "display-name": { "default": "Sample Entity" },
              "names": [["samples", "one"]],
              "count": 7,
              "content": { "text": "hello world" }
            }
            """);

        var text = EntityTextProjection.ProjectText(data);

        Assert.Contains("Sample Entity", text, StringComparison.Ordinal);
        Assert.Contains("samples", text, StringComparison.Ordinal);
        Assert.Contains("hello world", text, StringComparison.Ordinal);
        // Numbers are not embedding text.
        Assert.DoesNotContain("7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectText_StripsNonTextMimeContent_ButKeepsSiblingText()
    {
        var data = Parse(
            """
            {
              "display-name": { "default": "An Image" },
              "content": {
                "mime-type": "image/png",
                "content": "iVBORw0KGgoAAAANSUhEUgAAAAUA"
              }
            }
            """);

        var text = EntityTextProjection.ProjectText(data);

        Assert.Contains("An Image", text, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBORw0KGgo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectText_KeepsTextMimeContent()
    {
        var data = Parse(
            """
            { "content": { "mime-type": "text/markdown", "content": { "text": "# Heading" } } }
            """);

        Assert.Contains("Heading", EntityTextProjection.ProjectText(data), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectText_StripsDataUris()
    {
        var data = Parse("""{ "icon": "data:image/png;base64,AAAA" }""");
        Assert.Equal(string.Empty, EntityTextProjection.ProjectText(data));
    }

    [Fact]
    public void ProjectText_IsDeterministic()
    {
        var data = Parse("""{ "a": "alpha", "b": "beta" }""");
        Assert.Equal(EntityTextProjection.ProjectText(data), EntityTextProjection.ProjectText(data));
    }

    [Fact]
    public void ProjectText_NullData_IsEmpty()
    {
        Assert.Equal(string.Empty, EntityTextProjection.ProjectText(null));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class DeterministicEmbeddingsProviderTests
{
    [Fact]
    public void ModelIdAndDimensions_AreExposed()
    {
        var provider = new DeterministicEmbeddingsProvider(dimensions: 128, modelId: "test-model");
        Assert.Equal(128, provider.Dimensions);
        Assert.Equal("test-model", provider.ModelId);
    }

    [Fact]
    public async Task ComputeAsync_ProducesNormalizedVectorsOfTheRightDimension()
    {
        var provider = new DeterministicEmbeddingsProvider(dimensions: 64);

        var embeddings = await provider.ComputeAsync([Input('1', "the quick brown fox")]);

        var embedding = Assert.Single(embeddings);
        Assert.Equal(64, embedding.Values.Count);
        var magnitude = Math.Sqrt(embedding.Values.Sum(value => value * (double)value));
        Assert.Equal(1.0, magnitude, precision: 5);
    }

    [Fact]
    public async Task ComputeAsync_IsDeterministic_ForTheSameText()
    {
        var provider = new DeterministicEmbeddingsProvider();

        var first = await provider.ComputeAsync([Input('1', "semantic search over entities")]);
        var second = await provider.ComputeAsync([Input('2', "semantic search over entities")]);

        Assert.Equal(first[0].Values, second[0].Values);
    }

    [Fact]
    public async Task ComputeAsync_EmptyText_ProducesZeroVector()
    {
        var provider = new DeterministicEmbeddingsProvider(dimensions: 32);

        var embeddings = await provider.ComputeAsync([Input('1', "   ")]);

        Assert.All(embeddings[0].Values, component => Assert.Equal(0f, component));
    }

    [Fact]
    public async Task ComputeAsync_SimilarText_ScoresHigherThanDissimilar()
    {
        var provider = new DeterministicEmbeddingsProvider(dimensions: 512);

        var embeddings = await provider.ComputeAsync(
        [
            Input('0', "vector search over workspace entities"),
            Input('1', "vector search across workspace entities"),
            Input('2', "completely unrelated cooking recipe ingredients"),
        ]);

        var similarScore = CosineSimilarity(embeddings[0].Values, embeddings[1].Values);
        var differentScore = CosineSimilarity(embeddings[0].Values, embeddings[2].Values);

        Assert.True(
            similarScore > differentScore,
            $"Expected similar ({similarScore}) > different ({differentScore}).");
    }

    [Fact]
    public async Task ComputeAsync_PreservesInputOrderAndEntityIds()
    {
        var provider = new DeterministicEmbeddingsProvider();

        var embeddings = await provider.ComputeAsync([Input('a', "one"), Input('b', "two")]);

        Assert.Equal(EntityIdFor('a'), embeddings[0].EntityId);
        Assert.Equal(EntityIdFor('b'), embeddings[1].EntityId);
    }

    private static EmbeddingInput Input(char hexId, string text) => new()
    {
        EntityId = EntityIdFor(hexId),
        Text = text,
    };

    private static EntityId EntityIdFor(char hexId)
        => new(Guid.Parse($"00000000-0000-0000-0000-00000000000{hexId}"));

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * (double)right[index];
        }

        return dot;
    }
}
