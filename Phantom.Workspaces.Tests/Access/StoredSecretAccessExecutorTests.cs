using System.IO;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class StoredSecretAccessExecutorTests
{
    [Fact]
    public async Task StoredSecretExecutor_InspectAsync_DoesNotReadPlatformOrEnvironmentValue()
    {
        var source = new Source();
        await using var executor = new StoredSecretAccessExecutor(source);
        var request = Request();
        var inspection = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, inspection.Sources.Count);
        Assert.Equal(2, source.Inspections);
        Assert.Empty(source.Reads);
        Assert.Empty(inspection.CachedGenerations);
    }

    [Fact]
    public async Task StoredSecretExecutor_AllowAsync_WaitsForPendingRowAfterAsynchronousInspection()
    {
        var inspection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { InspectionGate = inspection.Task };
        await using var executor = new StoredSecretAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var ownedBroker = broker;
        await using var ownedRegistry = registry;
        var acquisition = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var allowing = AllowAsync(registry, CredentialSourceKind.Environment);
        Assert.False(allowing.IsCompleted);
        Assert.Empty(source.Reads);

        inspection.SetResult();
        await allowing;
        await using var result = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await acquisition).Result;
        Assert.Equal(new[] { CredentialSourceKind.Environment }, source.Reads);
    }

    [Theory]
    [InlineData(CredentialSourceKind.Environment)]
    [InlineData(CredentialSourceKind.SecretStore)]
    public async Task StoredSecretExecutor_ExecuteAsync_ReadsExactSourceAfterReleaseCommit(CredentialSourceKind selected)
    {
        var source = new Source();
        await using var executor = new StoredSecretAccessExecutor(source);
        var request = Request();
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(source.Reads);
        await AllowAsync(registry, selected);
        await using var result = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await task).Result;
        Assert.Equal(new[] { selected }, source.Reads);
        var use = await result.Lease.UseAsync((value, _) => ValueTask.FromResult(value), TestContext.Current.CancellationToken);
        Assert.Equal(selected.ToString(), Assert.IsType<SensitiveValueUseSucceeded<string>>(use).Result);
    }

    [Fact]
    public async Task StoredSecretExecutor_SelectedSourceMissing_ReturnsRenewalNeeded()
    {
        var source = new Source();
        await using var executor = new StoredSecretAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        source.MissingOnRead = true;
        await AllowAsync(registry, CredentialSourceKind.Environment);
        Assert.IsType<AccessReacquisitionRequired<StoredSecretAccessResult>>(await task);
        Assert.Equal(UserAuthorizationState.RenewalNeeded, Assert.Single(registry.GetSnapshot()).State);
        Assert.Equal(new[] { CredentialSourceKind.Environment }, source.Reads);
    }

    [Theory]
    [InlineData(CredentialSourceKind.Environment)]
    [InlineData(CredentialSourceKind.SecretStore)]
    public async Task StoredSecretExecutor_MissingSourceInspection_InvalidatesOnlyItsCachedGenerationWithoutReadingValues(
        CredentialSourceKind missing)
    {
        var source = new Source();
        await using var executor = new StoredSecretAccessExecutor(source);
        var request = Request();
        var (broker, registry) = Create(executor);
        await using var ownedBroker = broker;
        await using var ownedRegistry = registry;
        var environmentTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        await using var environment = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await environmentTask).Result;
        var storedTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.SecretStore);
        await using var stored = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await storedTask).Result;
        var initial = await executor.InspectCachedGenerationsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, initial.Count);
        ICredentialCacheAdministration administration = executor;
        Assert.Equal(2, (await administration.ListAsync(TestContext.Current.CancellationToken)).Count);
        source.MissingOnInspection.Add(missing);

        var inspection = await executor.InspectAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialSourceUsability.Missing, inspection.Sources.Single(choice => choice.Kind == missing).Usability);
        Assert.Equal(CredentialSourceUsability.Usable, inspection.Sources.Single(choice => choice.Kind != missing).Usability);
        var missingIdentifier = request.Sources.Single(choice => choice.Kind == missing).Identifier;
        var survivingIdentifier = request.Sources.Single(choice => choice.Kind != missing).Identifier;
        Assert.Equal(survivingIdentifier, Assert.Single(inspection.CachedGenerations).Key);
        Assert.Null(await executor.GetGenerationCache(missingIdentifier).InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(initial[survivingIdentifier],
            await executor.GetGenerationCache(survivingIdentifier).InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(initial[survivingIdentifier].Generation,
            Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken)).Generation);
        var callbacks = 0;
        var rejectedLease = missing == CredentialSourceKind.Environment ? environment.Lease : stored.Lease;
        var survivingLease = missing == CredentialSourceKind.Environment ? stored.Lease : environment.Lease;
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await rejectedLease.UseAsync((_, _) =>
            ValueTask.FromResult(++callbacks), TestContext.Current.CancellationToken));
        Assert.Equal(0, callbacks);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await survivingLease.UseAsync((_, _) =>
            ValueTask.FromResult(++callbacks), TestContext.Current.CancellationToken));
        Assert.Equal(1, callbacks);
        Assert.Equal(new[] { CredentialSourceKind.Environment, CredentialSourceKind.SecretStore }, source.Reads);
    }

    [Fact]
    public async Task StoredSecretExecutor_IndependentLeases_ExactGenerationDeletionDoesNotDeleteRotation()
    {
        var source = new Source();
        await using var executor = new StoredSecretAccessExecutor(source);
        var request = Request() with { Sources = [Request().Sources[0]] };
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        var first = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await firstTask).Result;
        var cache = executor.GetGenerationCache(request.Sources[0].Identifier);
        var initial = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        var secondTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        await using var second = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await secondTask).Result;
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        source.Suffix = "-rotated";
        var thirdTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        await using var third = Assert.IsType<AccessSucceeded<StoredSecretAccessResult>>(await thirdTask).Result;
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await cache.InvalidateGenerationAsync(initial.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await third.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        var current = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache.InvalidateGenerationAsync(current.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await third.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    internal static StoredSecretAccessRequest Request()
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "stored-tests"),
            Kind = AccessRequestorKind.Application,
            SafeDisplayName = "Application",
        };
        var lineage = new CredentialUseLineage();
        return new StoredSecretAccessRequest
        {
            Reference = new SecretReference { Name = "TEST_SECRET" },
            Credential = new CredentialReference
            {
                LogicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(new()
                {
                    Provider = CredentialProviderKind.StoredSecret,
                    Kind = CredentialKind.Secret,
                    CredentialPartition = "TEST_SECRET",
                    SourceCompatibility = "stored",
                    Authority = "",
                    Audience = "",
                    ClientPartition = "",
                    Scopes = [],
                }),
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
            },
            Requestor = requestor,
            Use = new CredentialUseDescriptor
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Application, lineage, requestor.Identifier, "read"),
                Kind = CredentialUseKind.Application,
                Lineage = lineage,
                SafeDisplayName = "Use stored secret",
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = new[] { CredentialSourceKind.Environment, CredentialSourceKind.SecretStore }.Select(kind => new CredentialSourceChoiceDescriptor
            {
                Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.StoredSecret, $"{kind}:TEST_SECRET"),
                Provider = CredentialProviderKind.StoredSecret,
                Kind = kind,
                Usability = CredentialSourceUsability.Missing,
                SafeDisplayName = kind.ToString(),
            }).ToArray(),
        };
    }

    internal static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(
        StoredSecretAccessExecutor executor,
        string? releaseMemoryPath = null)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(executor);
        var store = new ReleaseMemoryStore(releaseMemoryPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "stored-secret-access-tests",
            $"{Guid.NewGuid():N}.json"));
        return (new AccessBroker(new ReleaseAuthorizationPolicy(store), executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    internal static async Task AllowAsync(
        UserAuthorizationRegistry registry,
        CredentialSourceKind selected)
    {
        var pending = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObservePending()
        {
            if (registry.GetSnapshot().LastOrDefault(snapshot => snapshot.State == UserAuthorizationState.Pending) is { } row)
                pending.TrySetResult(row);
        }
        void Changed(object? sender, UserAuthorizationChangedEventArgs args) => ObservePending();
        registry.Changed += Changed;
        try
        {
            ObservePending();
            var row = await pending.Task.WaitAsync(TestContext.Current.CancellationToken);
            await registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Memory = row.SelectedMemory,
                Source = row.SourceOptions.Single(source => source.Kind == selected).Identifier,
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            registry.Changed -= Changed;
        }
    }

    private sealed class Source : IStoredSecretAccessSource
    {
        public int Inspections { get; private set; }
        public List<CredentialSourceKind> Reads { get; } = [];
        public bool MissingOnRead { get; set; }
        public HashSet<CredentialSourceKind> MissingOnInspection { get; } = [];
        public string Suffix { get; set; } = "";
        public Task? InspectionGate { get; init; }

        public async ValueTask<bool> ExistsAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Inspections++;
            if (this.InspectionGate is { } gate)
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return !this.MissingOnInspection.Contains(source);
        }

        public ValueTask<char[]?> ReadAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Reads.Add(source);
            return ValueTask.FromResult(this.MissingOnRead ? null : (source + this.Suffix).ToCharArray());
        }
    }
}
