using System.Diagnostics;
using System.Collections.Concurrent;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ProcessAcceptance;

namespace Phantom.Workspaces.Tests;

public sealed class PreparedRepositoryTests
{
    [Fact]
    public async Task PreparedGitReopenRetainsSessionAndDoesNotProvisionSchemasOrDiscoverIdentities()
    {
        var directory = ProcessStageDiagnostics.GetFixtureDirectory(AppContext.BaseDirectory);
        var source = new LocalGitRepositorySource(Path.Combine(directory, "entities"));
        var stages = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == RepositoryStartupDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stages.Enqueue(activity.OperationName),
        };
        ActivitySource.AddActivityListener(listener);
        WorkspaceEntitySession session;
        int entityCount;
        await using (var ordinary = await EntityRepository.CreateAsync(source))
        {
            session = ordinary.WorkspaceEntitySession;
            entityCount = (await ordinary.ExportEntitySnapshotsAsync(TestContext.Current.CancellationToken)).Count;
        }
        Assert.Contains("repository-schema-population", stages);
        Assert.Contains("repository-session-bootstrap", stages);
        stages.Clear();
        var validations = new List<SchemaValidationPass>();
        await using var reopened = await EntityRepository.CreateAsync(
            new EntityRepositoryInitializationOptions
            {
                RepositorySource = source,
                PreparedSession = session,
                ValidationPassStarted = validations.Add,
            }, TestContext.Current.CancellationToken);
        Assert.Same(session, reopened.WorkspaceEntitySession);
        Assert.Equal(entityCount, (await reopened.ExportEntitySnapshotsAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Empty(validations);
        Assert.DoesNotContain("repository-schema-population", stages);
        Assert.DoesNotContain("repository-session-bootstrap", stages);
        Assert.DoesNotContain("git-update", stages);
    }

    [Fact]
    public async Task MissingPreparedProfileFailsRatherThanSilentlyRediscoveringIdentity()
    {
        var profile = new EntityId(Guid.NewGuid());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EntityRepository.CreateAsync(
            new EntityRepositoryInitializationOptions
            {
                RepositorySource = new UnknownRepositorySource(),
                PreparedSession = new WorkspaceEntitySession
                {
                    UserEntityId = new EntityId(Guid.NewGuid()),
                    ComputerEntityId = profile,
                    UserComputerProfileEntityId = profile,
                },
                ValidationPassStarted = _ => Assert.Fail("Prepared reopen must not install schema."),
            }, TestContext.Current.CancellationToken));
        Assert.Contains("prepared repository profile", error.Message);
    }
}
