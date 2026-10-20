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

    internal static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(StoredSecretAccessExecutor executor)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(executor);
        return (new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore()), executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    internal static async Task AllowAsync(
        UserAuthorizationRegistry registry,
        CredentialSourceKind selected)
    {
        var row = registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SourceOptions.Single(source => source.Kind == selected).Identifier,
        }, TestContext.Current.CancellationToken);
    }

    private sealed class Source : IStoredSecretAccessSource
    {
        public int Inspections { get; private set; }
        public List<CredentialSourceKind> Reads { get; } = [];
        public bool MissingOnRead { get; set; }
        public string Suffix { get; set; } = "";

        public ValueTask<bool> ExistsAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Inspections++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<char[]?> ReadAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Reads.Add(source);
            return ValueTask.FromResult(this.MissingOnRead ? null : (source + this.Suffix).ToCharArray());
        }
    }
}
