using System.Diagnostics;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Tests;

public sealed class RepositoryStartupDiagnosticsTests
{
    [Fact]
    public async Task SchemaPopulation_EmitsNestedStageTimingsWithoutEntityPayloads()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RepositoryStartupDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var errors = await new SchemaPopulator(new InMemoryDataAccessLayer()).Populate();

        Assert.Empty(errors);
        var population = Assert.Single(activities, activity => activity.OperationName == "schema-population");
        foreach (var stage in new[] { "schema-load-embedded-resources", "schema-read-concurrency-tags", "schema-update" })
        {
            var activity = Assert.Single(activities, activity => activity.OperationName == stage);
            Assert.Equal(population.SpanId, activity.ParentSpanId);
            Assert.True(activity.Duration >= TimeSpan.Zero);
            Assert.Empty(activity.Tags);
            Assert.True(activities.IndexOf(activity) < activities.IndexOf(population));
        }
    }

    [Fact]
    public void UnsubscribedStartupTracing_DoesNotChangeExistingActivityContext()
    {
        using var parent = new Activity("test-parent").Start();
        var previous = Activity.Current;
        using (RepositoryStartupDiagnostics.Start("repository-initialization"))
        {
            Assert.Same(previous, Activity.Current);
        }
        Assert.Same(previous, Activity.Current);
    }

    [Fact]
    public async Task SchemaValidation_EmitsCountsAndNestedStageTimingsWithoutEntityPayloads()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RepositoryStartupDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var errors = await new SchemaPopulator(
            new SchemaValidatingDataAccessLayer(new InMemoryDataAccessLayer())).Populate();

        Assert.Empty(errors);
        var validation = Assert.Single(activities, activity => activity.OperationName == "schema-validation");
        Assert.Equal(Assert.Single(activities, activity => activity.OperationName == "schema-update").SpanId,
            validation.ParentSpanId);
        var allowedTags = new[]
        {
            "schema.composition.build_count", "schema.composition.cache_hit_count", "schema.evaluation.count",
        };
        Assert.Equal(allowedTags.Order(), validation.TagObjects.Select(tag => tag.Key).Order());
        Assert.All(validation.TagObjects, tag => Assert.True(Assert.IsType<int>(tag.Value) > 0));
        var stages = activities.Where(activity =>
            activity.OperationName is "schema-composition-build" or "schema-evaluation").ToArray();
        Assert.NotEmpty(stages);
        Assert.All(stages, activity =>
        {
            Assert.Equal(validation.SpanId, activity.ParentSpanId);
            Assert.True(activity.Duration >= TimeSpan.Zero);
            Assert.Empty(activity.TagObjects);
            Assert.Empty(activity.Events);
            Assert.Empty(activity.Baggage);
        });
        Assert.All(activities.Where(activity => activity != validation),
            activity => Assert.Empty(activity.TagObjects));
    }
}
