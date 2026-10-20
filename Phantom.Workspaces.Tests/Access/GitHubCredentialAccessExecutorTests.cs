using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubCredentialAccessExecutorTests
{
    [Fact]
    public async Task GitHubExecutor_RenewalAccountSwitch_FailsClosedAndPreservesApprovedAccountPin()
    {
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "provider-principal-A");
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "provider-principal-B");
        var source = new Source { Account = accountA };
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var first = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result;
        var cache = executor.GetGenerationCache(request.Sources[0].Identifier);
        var firstGeneration = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(accountA, firstGeneration.AccountIdentifier);
        await cache.InvalidateGenerationAsync(firstGeneration.Generation, TestContext.Current.CancellationToken);

        source.Account = accountB;
        var switchedTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        var switched = Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await switchedTask);
        Assert.Equal(AccessErrorCode.IdentityMismatch, switched.ErrorCode);
        Assert.Null(await cache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(accountA, (await executor.InspectAsync(request, TestContext.Current.CancellationToken)).Credential.AccountIdentifier);

        source.Account = accountA;
        var renewedTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var renewed = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await renewedTask).Result;
        var renewedGeneration = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(accountA, renewedGeneration.AccountIdentifier);
        Assert.NotEqual(firstGeneration.Generation, renewedGeneration.Generation);
        Assert.Equal(3, source.Acquisitions.Count);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await renewed.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await first.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubExecutor_CompatibleRequests_CoalesceProviderWorkButKeepIndependentApprovalsAndLeases()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Started = started, Continue = complete };
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var coordinator = new RecordingCoordinator();
        var (broker, registry) = Create(executor, coordinator: coordinator);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var secondTask = broker.AcquireAsync(request with
        {
            Use = request.Use with
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(request.Use.Kind, request.Use.Lineage, request.Requestor.Identifier, "independent-use"),
            },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, registry.GetSnapshot().Count);
        var firstRow = registry.GetSnapshot()[0];
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = firstRow.Identifier,
            ExpectedRevision = firstRow.Revision,
            Memory = firstRow.SelectedMemory,
            Source = firstRow.SelectedSource,
        }, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(UserAuthorizationState.Pending, registry.GetSnapshot()[1].State);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await coordinator.Joined.Task.WaitAsync(TestContext.Current.CancellationToken);
        complete.SetResult();
        var first = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result;
        await using var second = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await secondTask).Result;
        Assert.NotSame(first.Lease, second.Lease);
        Assert.Single(source.Acquisitions);
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubExecutor_CachedAccountMetadata_MatchesMemoryAndDeletionStillRequiresPerform()
    {
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, ".access-github-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = new Source();
            await using var executor = new GitHubCredentialAccessExecutor(source);
            var (broker, registry) = Create(executor, System.IO.Path.Combine(directory, "memory.json"));
            await using var owned = broker;
            var request = Request(CredentialSourceKind.GitHubCli);
            request = request with { Use = request.Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllUses, ReleaseMemoryScopeKind.AllowOnce] } };
            var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
            await AllowAsync(registry, CredentialSourceKind.GitHubCli);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result.DisposeAsync();
            await using var second = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(
                await broker.AcquireAsync(request, TestContext.Current.CancellationToken)).Result;
            Assert.Single(registry.GetSnapshot());
            var cache = executor.GetGenerationCache(request.Sources[0].Identifier);
            var generation = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
            await cache.InvalidateGenerationAsync(generation, TestContext.Current.CancellationToken);
            var thirdTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
            Assert.False(thirdTask.IsCompleted);
            var row = registry.GetSnapshot().Last();
            Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
            Assert.Single(source.Acquisitions);
            await registry.ActAsync(new DenyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
            }, TestContext.Current.CancellationToken);
            Assert.IsType<AccessDenied<GitHubCredentialAccessResult>>(await thirdTask);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GitHubExecutor_InspectAsync_DoesNotReadEnvironmentOrInvokeCli()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var inspection = await executor.InspectAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(2, inspection.Sources.Count);
        Assert.Empty(source.Acquisitions);
        Assert.Empty(inspection.CachedGenerations);
        Assert.All(inspection.Sources, choice => Assert.Equal(CredentialSourceUsability.AcquisitionRequired, choice.Usability));
    }

    [Theory]
    [InlineData(CredentialSourceKind.Environment)]
    [InlineData(CredentialSourceKind.GitHubCli)]
    public async Task GitHubExecutor_ExecuteAsync_GhCliRunsOnlyAfterReleaseCommit(CredentialSourceKind selected)
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(selected), TestContext.Current.CancellationToken);
        Assert.Empty(source.Acquisitions);
        await AllowAsync(registry, selected);
        await using var result = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await task).Result;
        Assert.Equal(new[] { selected }, source.Acquisitions);
        var use = await result.Lease.UseAsync((value, _) => ValueTask.FromResult(value), TestContext.Current.CancellationToken);
        Assert.Equal("private-test-token", Assert.IsType<SensitiveValueUseSucceeded<string>>(use).Result);
        Assert.DoesNotContain("private-test-token", System.Text.Json.JsonSerializer.Serialize(registry.GetSnapshot()));
        Assert.DoesNotContain("private-account", System.Text.Json.JsonSerializer.Serialize(registry.GetSnapshot()));
    }

    [Fact]
    public async Task GitHubExecutor_ResolvedAccountMismatch_FailsClosedWithoutLoggingToken()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var task = broker.AcquireAsync(request with
        {
            Credential = request.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "different-account"),
            },
        }, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        var failure = Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await task);
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.DoesNotContain("private", failure.SafeMessage);
        Assert.Null(await executor.GetGenerationCache(request.Sources[0].Identifier).InspectGenerationAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubExecutor_ForcedEnvironmentMissing_DoesNotFallbackToCli()
    {
        var source = new Source { HasEnvironment = false };
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(CredentialSourceKind.Environment), TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await task);
        Assert.Equal(new[] { CredentialSourceKind.Environment }, source.Acquisitions);
    }

    [Fact]
    public async Task GitHubExecutor_CacheDeletion_InvalidatesOldLeasesButNotIndependentCallerOwnership()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        var first = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result;
        var secondTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var second = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await secondTask).Result;
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.Single(source.Acquisitions);
        var cache = executor.GetGenerationCache(request.Sources[0].Identifier);
        var original = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache.InvalidateGenerationAsync(original.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await second.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        var nextTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var next = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await nextTask).Result;
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await cache.InvalidateGenerationAsync(original.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await next.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    internal static GitHubCredentialAccessRequest Request(CredentialSourceKind? source = null)
    {
        var request = GitHubCredentialAccessRequest.Create(
            GitHubCredentialPurpose.Repository,
            new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "github-tests"),
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            },
            new CredentialUseLineage(),
            "read",
            source: source);
        return request with
        {
            Use = request.Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce] },
        };
    }

    private static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(GitHubCredentialAccessExecutor executor)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(executor);
        return (new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore()), executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    private static async Task AllowAsync(
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

    private sealed class Source : IGitHubCredentialAccessSource
    {
        public bool HasEnvironment { get; set; } = true;
        public CredentialAccountIdentifier Account { get; set; } = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "private-account");
        public List<CredentialSourceKind> Acquisitions { get; } = [];

        public bool EnvironmentExists() => this.HasEnvironment;

        public ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            this.Acquisitions.Add(source);
            return ValueTask.FromResult(source == CredentialSourceKind.Environment && !this.HasEnvironment ? null : new GitHubCredentialMaterial
            {
                Material = "private-test-token".ToCharArray(),
                Account = this.Account,
            });
        }
    }
}
