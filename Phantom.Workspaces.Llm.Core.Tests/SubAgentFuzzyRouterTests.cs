using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class SubAgentFuzzyRouterTests
{
    private static readonly DeterministicEmbeddingsProvider EmbeddingsProvider = new();

    private static IReadOnlyList<float> Embed(string text)
    {
        var result = EmbeddingsProvider
            .ComputeAsync([new EmbeddingInput { EntityId = new EntityId(Guid.NewGuid()), Text = text }])
            .GetAwaiter()
            .GetResult();
        return result[0].Values;
    }

    private static FuzzyRouteCandidate Candidate(string id, string description, DateTimeOffset lastUpdated) => new()
    {
        Id = id,
        Description = description,
        DescriptionEmbedding = Embed(description),
        LastUpdated = lastUpdated,
    };

    private static SubAgentFuzzyRouter CreateRouter(DateTimeOffset now, SubAgentDispatcherOptions? options = null)
    {
        return new SubAgentFuzzyRouter(
            EmbeddingsProvider,
            options ?? new SubAgentDispatcherOptions { AgentDefinitionTools = [] },
            new FixedTimeProvider(now));
    }

    [Fact]
    public async Task ClearWinner_RoutesToBestCandidate()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);
        var candidates = new[]
        {
            Candidate("widget-agent", "widget widget rendering layout painting", now),
            Candidate("db-agent", "database migration schema index tuning", now),
        };

        var result = await router.RouteAsync("widget", candidates, CancellationToken.None);

        var match = Assert.IsType<FuzzyRouteMatch>(result);
        Assert.Equal("widget-agent", match.Id);
    }

    [Fact]
    public async Task CloseScoringCandidates_YieldDisambiguation()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);
        var candidates = new[]
        {
            Candidate("shared-alpha", "shared alpha beta", now),
            Candidate("shared-gamma", "shared gamma delta", now),
        };

        var result = await router.RouteAsync("shared", candidates, CancellationToken.None);

        var ambiguous = Assert.IsType<FuzzyRouteAmbiguous>(result);
        Assert.StartsWith("Ambiguous sub-agent identifier \"shared\". Matching agents:\n", ambiguous.Message);
        Assert.EndsWith("Please resubmit with the explicit agent ID.", ambiguous.Message);
        Assert.Contains("shared-alpha (last updated ", ambiguous.Message);
        Assert.Contains("shared-gamma (last updated ", ambiguous.Message);
    }

    [Fact]
    public async Task Disambiguation_ShowsAtMostThreeCandidates()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);
        var candidates = new[]
        {
            Candidate("shared-one", "shared alpha one", now),
            Candidate("shared-two", "shared alpha two", now),
            Candidate("shared-three", "shared alpha three", now),
            Candidate("shared-four", "shared alpha four", now),
        };

        var result = await router.RouteAsync("shared", candidates, CancellationToken.None);

        var ambiguous = Assert.IsType<FuzzyRouteAmbiguous>(result);
        var lines = ambiguous.Message.Split('\n');

        // Header + at most 3 candidates + trailing line.
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("Ambiguous sub-agent identifier \"shared\". Matching agents:", lines[0]);
        Assert.Equal("Please resubmit with the explicit agent ID.", lines[^1]);
    }

    [Fact]
    public async Task Disambiguation_TruncatesDescription_AndFormatsTimestamp()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);
        var lastUpdated = now - TimeSpan.FromHours(72);
        var longDescription = "shared bug to discover foo bar baz and then keep investigating everything else too";
        Assert.True(longDescription.Length > 60);

        // A single stale candidate forces a disambiguation response (recency bias) whose one line
        // exercises truncation and timestamp formatting deterministically.
        var candidates = new[]
        {
            Candidate("shared-one", longDescription, lastUpdated),
        };

        var result = await router.RouteAsync("shared", candidates, CancellationToken.None);

        var ambiguous = Assert.IsType<FuzzyRouteAmbiguous>(result);

        var expectedTimestamp = lastUpdated.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz");
        Assert.Contains($"shared-one (last updated {expectedTimestamp}): ", ambiguous.Message);

        var expectedTruncated = longDescription[..60] + "\u2026";
        Assert.Contains($"\"{expectedTruncated}\"", ambiguous.Message);
    }

    [Fact]
    public async Task StaleBestMatch_YieldsDisambiguationDespiteHighScore()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);
        var staleTimestamp = now - TimeSpan.FromHours(72);
        var candidates = new[]
        {
            Candidate("widget-agent", "widget widget rendering layout painting", staleTimestamp),
        };

        var result = await router.RouteAsync("widget", candidates, CancellationToken.None);

        var ambiguous = Assert.IsType<FuzzyRouteAmbiguous>(result);
        Assert.Contains("widget-agent (last updated ", ambiguous.Message);
    }

    [Fact]
    public async Task FreshSingleCandidate_RoutesDirectly()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);
        var candidates = new[]
        {
            Candidate("widget-agent", "widget widget rendering layout painting", now - TimeSpan.FromHours(1)),
        };

        var result = await router.RouteAsync("widget", candidates, CancellationToken.None);

        var match = Assert.IsType<FuzzyRouteMatch>(result);
        Assert.Equal("widget-agent", match.Id);
    }

    [Fact]
    public async Task NoCandidates_YieldsDisambiguation()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var router = CreateRouter(now);

        var result = await router.RouteAsync("anything", [], CancellationToken.None);

        var ambiguous = Assert.IsType<FuzzyRouteAmbiguous>(result);
        Assert.EndsWith("Please resubmit with the explicit agent ID.", ambiguous.Message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
