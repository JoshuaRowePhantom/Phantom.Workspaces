using Avalonia.Headless.XUnit;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Phantom.Workspaces.Data;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class EntityRepositoryTests
{
    [AvaloniaFact]
    public async Task CreateAsync_InMemoryEagerStartup_ValidatesEmbeddedSeedExactlyOnce()
    {
        var validationPasses = new ConcurrentQueue<SchemaValidationPass>();

        await EntityRepository.CreateAsync(
            new EntityRepositoryInitializationOptions
            {
                RepositorySource = new UnknownRepositorySource(),
                ValidationPassStarted = validationPasses.Enqueue,
            },
            TestContext.Current.CancellationToken);

        var seedPass = Assert.Single(
            validationPasses,
            static pass => pass.Kind == SchemaValidationPassKind.TrustedEmbeddedSeed);
        Assert.True(seedPass.ChangeCount > 100);
    }

    [AvaloniaFact]
    public async Task CreateAsync_ConcurrentInMemoryInstances_ValidateSeedsIndependently()
    {
        var firstPasses = new ConcurrentQueue<SchemaValidationPass>();
        var secondPasses = new ConcurrentQueue<SchemaValidationPass>();

        var repositories = await Task.WhenAll(
            EntityRepository.CreateAsync(
                new EntityRepositoryInitializationOptions
                {
                    RepositorySource = new UnknownRepositorySource(),
                    ValidationPassStarted = firstPasses.Enqueue,
                },
                TestContext.Current.CancellationToken),
            EntityRepository.CreateAsync(
                new EntityRepositoryInitializationOptions
                {
                    RepositorySource = new UnknownRepositorySource(),
                    ValidationPassStarted = secondPasses.Enqueue,
                },
                TestContext.Current.CancellationToken));

        Assert.NotSame(repositories[0], repositories[1]);
        Assert.Single(firstPasses, static pass => pass.Kind == SchemaValidationPassKind.TrustedEmbeddedSeed);
        Assert.Single(secondPasses, static pass => pass.Kind == SchemaValidationPassKind.TrustedEmbeddedSeed);
    }

    [AvaloniaFact]
    public async Task CreateAsync_PreCancelled_DoesNotStartSeedValidation()
    {
        var validationPasses = new ConcurrentQueue<SchemaValidationPass>();
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EntityRepository.CreateAsync(
                new EntityRepositoryInitializationOptions
                {
                    RepositorySource = new UnknownRepositorySource(),
                    ValidationPassStarted = validationPasses.Enqueue,
                },
                cancellationSource.Token));

        Assert.Empty(validationPasses);
    }

    [AvaloniaFact]
    public async Task CreateAsync_RepeatedFreshInMemoryInstances_PreservesIndependentValidatedSeeds()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RepositoryStartupDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var repositories = new List<EntityRepository>();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var passes = new ConcurrentQueue<SchemaValidationPass>();
                repositories.Add(await EntityRepository.CreateAsync(
                    new EntityRepositoryInitializationOptions
                    {
                        RepositorySource = new UnknownRepositorySource(),
                        ValidationPassStarted = passes.Enqueue,
                    },
                    TestContext.Current.CancellationToken));
                var seed = Assert.Single(passes, pass => pass.Kind == SchemaValidationPassKind.TrustedEmbeddedSeed);
                Assert.True(seed.ChangeCount > 100);
            }

            var seedUpdates = activities.Where(activity => activity.OperationName == "schema-update")
                .Select(activity => activity.SpanId).ToHashSet();
            var validations = activities.Where(activity => activity.OperationName == "schema-validation"
                && seedUpdates.Contains(activity.ParentSpanId)).ToArray();
            Assert.Equal(3, validations.Length);
            foreach (var validation in validations)
            {
                var builds = Assert.IsType<int>(validation.GetTagItem("schema.composition.build_count"));
                var hits = Assert.IsType<int>(validation.GetTagItem("schema.composition.cache_hit_count"));
                var evaluations = Assert.IsType<int>(validation.GetTagItem("schema.evaluation.count"));
                var stages = activities.Where(activity => activity.ParentSpanId == validation.SpanId).ToArray();
                var buildTime = stages.Where(activity => activity.OperationName == "schema-composition-build")
                    .Sum(activity => activity.Duration.TotalMilliseconds);
                var evaluationTime = stages.Where(activity => activity.OperationName == "schema-evaluation")
                    .Sum(activity => activity.Duration.TotalMilliseconds);
                TestContext.Current.TestOutputHelper!.WriteLine(
                    $"Fresh seed: builds={builds}; hits={hits}; evaluations={evaluations}; build-ms={buildTime:F3}; evaluation-ms={evaluationTime:F3}; validation-ms={validation.Duration.TotalMilliseconds:F3}");
                Assert.Equal(evaluations, builds + hits);
                Assert.True(evaluations > 100);
            }
            Assert.All(validations, validation =>
            {
                var builds = Assert.IsType<int>(validation.GetTagItem("schema.composition.build_count"));
                var evaluations = Assert.IsType<int>(validation.GetTagItem("schema.evaluation.count"));
                Assert.True(builds < evaluations, $"builds={builds}; evaluations={evaluations}");
            });

            var id = new EntityId(Guid.NewGuid());
            await UpsertEntityAsync(repositories[0], id,
                $$"""{"entity-id":"{{id}}","entity-types":["entity","task"],"names":[["tests","independent-startup"]]}""",
                null);
            Assert.Contains(id, (await repositories[0].ExportEntitySnapshotsAsync()).Keys);
            Assert.DoesNotContain(id, (await repositories[1].ExportEntitySnapshotsAsync()).Keys);
            Assert.DoesNotContain(id, (await repositories[2].ExportEntitySnapshotsAsync()).Keys);
        }
        finally
        {
            foreach (var repository in repositories)
                await repository.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task TryGetEntityByName_FindsSeededMainView()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            ct);
        var repository = broker.EntityRepository;
        var snapshots = await repository.ExportEntitySnapshotsAsync(ct);

        var mainView = repository.TryGetEntityByName(
            snapshots,
            new EntityName("views", "main"));

        Assert.NotNull(mainView);
    }

    [AvaloniaFact]
    public async Task ExportEntitySnapshotsAsync_ReturnsLatestEntityVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            ct);
        var repository = broker.EntityRepository;
        var entityId = new EntityId("55555555-5555-5555-5555-555555555555");

        var firstConcurrencyTag = await UpsertEntityAsync(
            repository,
            entityId,
            """
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity", "task"],
              "names": [["tests", "export-version"]],
              "display-name": { "default": "Version 1" }
            }
            """,
            concurrencyTag: null);
        await UpsertEntityAsync(
            repository,
            entityId,
            """
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity", "task"],
              "names": [["tests", "export-version"]],
              "display-name": { "default": "Version 2" }
            }
            """,
            firstConcurrencyTag);

        var snapshots = await repository.ExportEntitySnapshotsAsync(ct);

        var snapshot = Assert.Contains(entityId, snapshots);
        Assert.Contains("Version 2", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CreateAsync_InitializesWorkspaceEntitySessionDiscoveryEntities()
    {
        var repository = await EntityRepository.CreateAsync(new UnknownRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();

        Assert.Contains(repository.WorkspaceEntitySession.UserEntityId, snapshots.Keys);
        Assert.Contains(repository.WorkspaceEntitySession.ComputerEntityId, snapshots.Keys);
        Assert.Contains(repository.WorkspaceEntitySession.UserComputerProfileEntityId, snapshots.Keys);
    }

    private static async Task<ConcurrencyTag?> UpsertEntityAsync(
        EntityRepository repository,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        var ct = TestContext.Current.CancellationToken;
        using var document = JsonDocument.Parse(json);
        var updateResult = await repository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Entity repository test upsert.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            },
            ct);

        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return entityResult.ConcurrencyTag;
    }
}
