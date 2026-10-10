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
}
