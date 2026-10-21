using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubCredentialAccessExecutorTests
{
    [Theory]
    [InlineData(ReleaseMemoryScopeKind.AllUses)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse)]
    public async Task GitHubExecutor_TwoAccountSources_UseOnlySelectedSourceMemoryAndPreserveExplicitPins(ReleaseMemoryScopeKind scope)
    {
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, ".access-github-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "remembered-environment-A");
            var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "remembered-cli-B");
            var source = new Source { Account = accountA, Material = "environment-material-A" };
            await using var executor = new GitHubCredentialAccessExecutor(source);
            var (broker, registry) = Create(executor, System.IO.Path.Combine(directory, "memory.json"));
            await using var owned = broker;
            var environment = broker.AcquireAsync(Request(CredentialSourceKind.Environment), TestContext.Current.CancellationToken);
            await AllowAsync(registry, CredentialSourceKind.Environment);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await environment).Result.DisposeAsync();
            source.Account = accountB;
            source.Material = "cli-material-B";
            var cliRequest = Request(CredentialSourceKind.GitHubCli);
            cliRequest = cliRequest with { Use = cliRequest.Use with { MemoryScopes = [scope, ReleaseMemoryScopeKind.AllowOnce] } };
            var cli = broker.AcquireAsync(cliRequest, TestContext.Current.CancellationToken);
            await AllowAsync(registry, CredentialSourceKind.GitHubCli);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await cli).Result.DisposeAsync();

            var factoryRequest = Request();
            factoryRequest = factoryRequest with { Use = factoryRequest.Use with { MemoryScopes = [scope, ReleaseMemoryScopeKind.AllowOnce] } };
            await using var remembered = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(
                await broker.AcquireAsync(factoryRequest, TestContext.Current.CancellationToken)).Result;
            var use = await remembered.Lease.UseAsync((material, _) => ValueTask.FromResult(material), TestContext.Current.CancellationToken);
            Assert.Equal("cli-material-B", Assert.IsType<SensitiveValueUseSucceeded<string>>(use).Result);
            Assert.Equal(2, registry.GetSnapshot().Count);
            Assert.Equal(2, source.Acquisitions.Count);

            var pinned = broker.AcquireAsync(factoryRequest with
            {
                Credential = factoryRequest.Credential with { AccountIdentifier = accountA },
            }, TestContext.Current.CancellationToken);
            Assert.False(pinned.IsCompleted);
            await AllowAsync(registry, CredentialSourceKind.GitHubCli);
            Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await pinned).ErrorCode);
            Assert.Equal(2, source.Acquisitions.Count);
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
    public async Task GitHubExecutor_CommittedReceiptCannotSubstituteLaterGenerationOrAccount()
    {
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "receipt-account-A");
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "receipt-account-B");
        var source = new Source { Account = accountA, Material = "material-account-A" };
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var coordinator = new PausedReceiptCoordinator();
        var (broker, registry) = Create(executor, coordinator: coordinator);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.GitHubCli);
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        var firstGeneration = await coordinator.Committed.Task.WaitAsync(TestContext.Current.CancellationToken);
        var cache = executor.GetGenerationCache(request.Sources[0].Identifier);
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache.InvalidateGenerationAsync(firstGeneration, TestContext.Current.CancellationToken));
        source.Account = accountB;
        source.Material = "material-account-B";
        var secondTask = broker.AcquireAsync(request with
        {
            Credential = request.Credential with { AccountIdentifier = accountB },
        }, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await using var second = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await secondTask).Result;
        var secondGeneration = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.NotEqual(firstGeneration, secondGeneration.Generation);
        Assert.Equal(accountB, secondGeneration.AccountIdentifier);
        coordinator.Continue.SetResult();
        Assert.IsType<AccessReacquisitionRequired<GitHubCredentialAccessResult>>(await firstTask);
        var secondUse = await second.Lease.UseAsync((material, _) => ValueTask.FromResult(material), TestContext.Current.CancellationToken);
        Assert.Equal("material-account-B", Assert.IsType<SensitiveValueUseSucceeded<string>>(secondUse).Result);
        Assert.Equal(2, source.Acquisitions.Count);
    }

    [Fact]
    public async Task GitHubExecutor_WarmEnvironmentRemoved_ExplicitPerformCannotReleaseOldCache()
    {
        var source = new Source();
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var request = Request(CredentialSourceKind.Environment);
        var firstTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        await using var first = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result;
        source.HasEnvironment = false;
        var secondTask = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = registry.GetSnapshot().Last();
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        Assert.IsType<AccessReacquisitionRequired<GitHubCredentialAccessResult>>(await secondTask);
        Assert.Single(source.Acquisitions);
        Assert.Empty(await executor.InspectCachedGenerationsAsync(TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await first.Lease.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ReleaseMemoryScopeKind.AllUses)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse)]
    public async Task GitHubExecutor_SubsequentFactoryRequest_UsesVerifiedAccountMemoryWithoutNewRow(ReleaseMemoryScopeKind scope)
    {
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, ".access-github-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = new Source { HasEnvironment = false };
            await using var executor = new GitHubCredentialAccessExecutor(source);
            var (broker, registry) = Create(executor, System.IO.Path.Combine(directory, "memory.json"));
            await using var owned = broker;
            var firstRequest = Request();
            firstRequest = firstRequest with { Use = firstRequest.Use with { MemoryScopes = [scope, ReleaseMemoryScopeKind.AllowOnce] } };
            var firstTask = broker.AcquireAsync(firstRequest, TestContext.Current.CancellationToken);
            await AllowAsync(registry, CredentialSourceKind.GitHubCli);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await firstTask).Result.DisposeAsync();
            var nextRequest = Request();
            nextRequest = nextRequest with { Use = nextRequest.Use with { MemoryScopes = [scope, ReleaseMemoryScopeKind.AllowOnce] } };
            await using var next = Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(
                await broker.AcquireAsync(nextRequest, TestContext.Current.CancellationToken)).Result;
            Assert.Single(registry.GetSnapshot());
            Assert.Single(source.Acquisitions);
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
    public async Task GitHubExecutor_TwoSourceAccounts_KeepMetadataSeparateAndNeverOverrideExplicitAccountPin()
    {
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "environment-principal-A");
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "cli-principal-B");
        var source = new Source { Account = accountA };
        await using var executor = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(executor);
        await using var owned = broker;
        var environment = broker.AcquireAsync(Request(CredentialSourceKind.Environment), TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.Environment);
        await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await environment).Result.DisposeAsync();
        source.Account = accountB;
        var cli = broker.AcquireAsync(Request(CredentialSourceKind.GitHubCli), TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await cli).Result.DisposeAsync();
        var request = Request();
        var inspection = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(inspection.Credential.AccountIdentifier);
        Assert.Equal(accountA, inspection.CachedGenerations[request.Sources[0].Identifier].AccountIdentifier);
        Assert.Equal(accountB, inspection.CachedGenerations[request.Sources[1].Identifier].AccountIdentifier);
        var pinned = request with { Credential = request.Credential with { AccountIdentifier = accountA } };
        Assert.Equal(accountA, (await executor.InspectAsync(pinned, TestContext.Current.CancellationToken)).Credential.AccountIdentifier);
        var denied = broker.AcquireAsync(pinned, TestContext.Current.CancellationToken);
        await AllowAsync(registry, CredentialSourceKind.GitHubCli);
        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<GitHubCredentialAccessResult>>(await denied).ErrorCode);
        Assert.Equal(2, source.Acquisitions.Count);
    }

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
        Assert.IsType<AccessReacquisitionRequired<GitHubCredentialAccessResult>>(await task);
        Assert.Empty(source.Acquisitions);
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

    private static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(
        GitHubCredentialAccessExecutor executor,
        string? memoryPath = null,
        IProviderAcquisitionCoordinator? coordinator = null)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(executor);
        return (new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(memoryPath)), executors, coordinator ?? new ProviderAcquisitionCoordinator(), registry), registry);
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

    private sealed class PausedReceiptCoordinator : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int receipts;
        public TaskCompletionSource<CredentialMaterialGenerationIdentifier> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var outcome = await this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (outcome is ProviderAcquisitionSucceeded succeeded && Interlocked.Increment(ref this.receipts) == 1)
            {
                this.Committed.TrySetResult(succeeded.CommittedGeneration);
                await this.Continue.Task.WaitAsync(cancellationToken);
            }
            return outcome;
        }

        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    private sealed class RecordingCoordinator : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int executions;
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var outcome = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (Interlocked.Increment(ref this.executions) == 2)
            {
                this.Joined.TrySetResult();
            }
            return outcome;
        }

        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    private sealed class Source : IGitHubCredentialAccessSource
    {
        public bool HasEnvironment { get; set; } = true;
        public CredentialAccountIdentifier Account { get; set; } = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "private-account");
        public string Material { get; set; } = "private-test-token";
        public List<CredentialSourceKind> Acquisitions { get; } = [];
        public TaskCompletionSource? Started { get; init; }
        public TaskCompletionSource? Continue { get; init; }

        public bool EnvironmentExists() => this.HasEnvironment;

        public async ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            this.Acquisitions.Add(source);
            this.Started?.TrySetResult();
            if (this.Continue is { } continuation)
            {
                await continuation.Task.WaitAsync(cancellationToken);
            }
            return source == CredentialSourceKind.Environment && !this.HasEnvironment ? null : new GitHubCredentialMaterial
            {
                Material = this.Material.ToCharArray(),
                Account = this.Account,
            };
        }
    }
}
