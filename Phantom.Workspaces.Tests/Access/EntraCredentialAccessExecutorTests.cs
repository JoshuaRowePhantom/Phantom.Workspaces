using System.Text.Json;
using System.IO;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class EntraCredentialAccessExecutorTests
{
    [Fact]
    public async Task EntraExecutor_FirstUse_CommitsReleaseBeforeInteraction()
    {
        await using var fixture = new Fixture();
        fixture.Acquirer.BeforeAcquire = async _ =>
        {
            Assert.Equal(1, fixture.Policy.Commits);
            Assert.Null(await fixture.Session.InspectGenerationAsync(CancellationToken.None));
        };
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var row = Assert.Single(fixture.Registry.GetSnapshot());
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        Assert.Equal(0, fixture.Acquirer.Calls);
        Assert.False(pending.IsCompleted);
        await fixture.ApplyAsync(row);
        await using var result = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.Equal(1, fixture.Policy.Finalizations);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await result.AccessToken.UseAsync((token, _) => ValueTask.FromResult(token.Length), TestContext.Current.CancellationToken));
        var safeRows = JsonSerializer.Serialize(fixture.Registry.GetSnapshot());
        Assert.DoesNotContain("private-token", safeRows);
        Assert.DoesNotContain("stable-test-account", safeRows);
    }

    [Fact]
    public async Task EntraExecutor_ExpiredCache_RequiresExplicitPerform()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Acquirer.ExpiresOn = fixture.Clock.GetUtcNow().AddHours(1);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var row = fixture.Registry.GetSnapshot().Last();
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        Assert.Equal(ReleaseMemoryScopeKind.AllUses,
            row.MemoryOptions.Single(option => option.Identifier == row.SelectedMemory).Scope);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, fixture.Acquirer.Calls);
        await fixture.ApplyAsync(row);
        await using var renewed = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(2, fixture.Acquirer.Calls);
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(
            await first.AccessToken.UseAsync<int>((_, _) => throw new InvalidOperationException("An expired lease cannot run."), TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Acquirer.Calls);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await renewed.AccessToken.UseAsync((token, _) => ValueTask.FromResult(token.Length), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EntraExecutor_FinalizesAccountBeforeIndependentLeaseMaterialization()
    {
        await using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Acquirer.BeforeAcquire = async cancellationToken =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
        };
        var firstPending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var secondPending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot()[0]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot()[1]);
        await fixture.Coordinator.FollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Policy.Finalizations);
        Assert.False(secondPending.IsCompleted);
        finish.TrySetResult();
        var first = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await firstPending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        await using var second = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await secondPending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(2, fixture.Policy.Finalizations);
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.NotSame(first.AccessToken, second.AccessToken);
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await second.AccessToken.UseAsync((token, _) => ValueTask.FromResult(token.Length), TestContext.Current.CancellationToken));
        Assert.Equal(fixture.Acquirer.Account, (await fixture.Session.InspectGenerationAsync(CancellationToken.None))!.AccountIdentifier);
    }

    [Fact]
    public async Task EntraExecutor_ValidRememberedCache_DoesNotCallProvider()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        await using var second = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.Single(fixture.Registry.GetSnapshot());
        Assert.NotSame(first.AccessToken, second.AccessToken);
        Assert.Equal(fixture.Acquirer.ExpiresOn, second.ExpiresOn);
    }

    [Fact]
    public async Task EntraExecutor_ReleaseRevocationStopsUseWithoutProviderInteraction()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        var memory = Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken));
        await fixture.Policy.ForgetAsync(memory.Identifier, TestContext.Current.CancellationToken);
        Assert.IsType<SensitiveValueUseAuthorizationRequired<int>>(
            await result.AccessToken.UseAsync<int>((_, _) => throw new InvalidOperationException("Revoked material cannot run."), TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, fixture.Registry.GetSnapshot().Last().PrimaryAction);
    }

    [Fact]
    public async Task EntraExecutor_ExactRejectedCache_RequiresNewPerformWithoutSwitchingOldLease()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        var generation = (await fixture.Session.InspectGenerationAsync(CancellationToken.None))!.Generation;
        Assert.Null(fixture.Executor.FindCache(
            fixture.Request.CreateLogicalIdentifier(),
            CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.Entra, "another-source")));
        var cache = fixture.Executor.FindCache(fixture.Request.CreateLogicalIdentifier(), fixture.Request.CreateSourceIdentifier());
        Assert.Same(fixture.Session, cache);
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache!.InvalidateGenerationAsync(generation, TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(
            await result.AccessToken.UseAsync<int>((_, _) => throw new InvalidOperationException("Rejected material cannot run."), TestContext.Current.CancellationToken));
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, fixture.Registry.GetSnapshot().Last().PrimaryAction);
        Assert.Equal(1, fixture.Acquirer.Calls);
    }

    [Fact]
    public async Task EntraExecutor_ProviderFailure_IsSafeAndDoesNotAutomaticallyRetry()
    {
        await using var fixture = new Fixture();
        fixture.Acquirer.Failure = new AuthenticationFailedException("private-token stable-test-account");
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));
        var failure = Assert.IsType<AccessFailed<EntraCredentialAccessResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(AccessErrorCode.ProviderFailure, failure.ErrorCode);
        Assert.DoesNotContain("private-token", failure.SafeMessage);
        Assert.DoesNotContain("stable-test-account", failure.SafeMessage);
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.Null(await fixture.Session.InspectGenerationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EntraExecutor_ExpectedAccountCannotBeReplacedByCachedAccount()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        var request = fixture.Request with
        {
            Credential = fixture.Request.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "different-account"),
            },
        };
        var pending = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = fixture.Registry.GetSnapshot().Last();
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        Assert.False(pending.IsCompleted);
        await fixture.ApplyAsync(row);
        var failure = Assert.IsType<AccessFailed<EntraCredentialAccessResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Equal(fixture.Acquirer.Account, (await fixture.Session.InspectGenerationAsync(CancellationToken.None))!.AccountIdentifier);
    }

    [Fact]
    public async Task EntraExecutor_RenewalCannotCommitAnotherAccountOverApprovedIdentity()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        var approved = await fixture.Session.InspectGenerationAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Acquirer.ExpiresOn = fixture.Clock.GetUtcNow().AddHours(1);
        fixture.Acquirer.Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "another-account");
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot().Last());
        var failure = Assert.IsType<AccessFailed<EntraCredentialAccessResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Equal(approved, await fixture.Session.InspectGenerationAsync(CancellationToken.None));
        Assert.Equal(2, fixture.Acquirer.Calls);
    }

    [Fact]
    public async Task EntraExecutor_CancelledLeaderDoesNotInvalidateApprovedFollowerCommit()
    {
        await using var fixture = new Fixture();
        using var leaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Acquirer.BeforeAcquire = async cancellationToken =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
        };
        var first = fixture.Broker.AcquireAsync(fixture.Request, leaderCancellation.Token);
        var second = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot()[0]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot()[1]);
        await fixture.Coordinator.FollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        leaderCancellation.Cancel();
        Assert.IsType<AccessCancelled<EntraCredentialAccessResult>>(
            await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        finish.TrySetResult();
        await using var result = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Acquirer.Calls);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await result.AccessToken.UseAsync((token, _) => ValueTask.FromResult(token.Length), TestContext.Current.CancellationToken));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".entra-executor-tests", Guid.NewGuid().ToString("N"));
        internal EntraCredentialAccessRequest Request { get; } = CreateRequest();
        internal FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        internal Acquirer Acquirer { get; } = new();
        internal IEntraCredentialSession Session { get; }
        internal TrackingPolicy Policy { get; }
        internal UserAuthorizationRegistry Registry { get; } = new();
        internal TrackingCoordinator Coordinator { get; } = new();
        internal EntraCredentialAccessExecutor Executor { get; }
        internal AccessBroker Broker { get; }

        internal Fixture()
        {
            this.Acquirer.ExpiresOn = this.Clock.GetUtcNow().AddHours(1);
            this.Session = new EntraCredentialSession(this.Request, new ForbiddenCredential(), this.Acquirer, this.Clock);
            this.Policy = new TrackingPolicy(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json"))));
            var executors = new CredentialAccessExecutorRegistry();
            this.Executor = new EntraCredentialAccessExecutor(_ => this.Session, this.Clock);
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(
                this.Executor);
            this.Broker = new AccessBroker(this.Policy, executors, this.Coordinator, this.Registry);
        }

        internal async Task<EntraCredentialAccessResult> AcquireAsync(ReleaseMemoryScopeKind scope)
        {
            var pending = this.Broker.AcquireAsync(this.Request);
            await this.ApplyAsync(this.Registry.GetSnapshot().Last(), scope);
            return Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
                await pending.WaitAsync(TimeSpan.FromSeconds(10))).Result;
        }

        internal Task ApplyAsync(
            UserAuthorizationSnapshot row,
            ReleaseMemoryScopeKind? scope = null) => this.Registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Source = row.SelectedSource,
                Memory = scope is null ? row.SelectedMemory : row.MemoryOptions.Single(option => option.Scope == scope).Identifier,
            });

        public async ValueTask DisposeAsync()
        {
            await this.Broker.DisposeAsync();
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, true);
            }
        }
    }

    [Fact]
    public async Task EntraExecutor_CacheAdministration_ListsAllOwnedSessionsWithoutProviderWork()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var acquirer = new Acquirer { ExpiresOn = clock.GetUtcNow().AddHours(1) };
        await using var executor = new EntraCredentialAccessExecutor(
            configuration => new EntraCredentialSession(configuration, new ForbiddenCredential(), acquirer, clock),
            clock);
        ICredentialCacheAdministration administration = executor;
        Assert.Empty(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, acquirer.Calls);
        var firstRequest = CreateRequest();
        var secondDraft = firstRequest with { CachePartition = new() { Value = "second-partition" } };
        var secondRequest = secondDraft with
        {
            Credential = secondDraft.Credential with { LogicalIdentifier = secondDraft.CreateLogicalIdentifier() },
            Sources = [secondDraft.Sources[0] with { Identifier = secondDraft.CreateSourceIdentifier() }],
        };
        var first = executor.GetSession(firstRequest);
        var second = executor.GetSession(secondRequest);
        Assert.Same(first, executor.GetSession(firstRequest));
        Assert.NotSame(first, second);
        Assert.Empty(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, acquirer.Calls);
        var firstGeneration = await CommitSessionAsync(first, firstRequest);
        var secondGeneration = await CommitSessionAsync(second, secondRequest);
        var descriptors = await administration.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, descriptors.Count);
        Assert.Contains(descriptors, descriptor =>
            descriptor.Generation == firstGeneration && descriptor.SourceIdentifier == firstRequest.CreateSourceIdentifier());
        Assert.Contains(descriptors, descriptor =>
            descriptor.Generation == secondGeneration && descriptor.SourceIdentifier == secondRequest.CreateSourceIdentifier());
        Assert.Equal(descriptors, await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, acquirer.Calls);
        var safeMetadata = JsonSerializer.Serialize(descriptors);
        Assert.DoesNotContain("private-token", safeMetadata);
        Assert.DoesNotContain("stable-test-account", safeMetadata);
    }

    [Fact]
    public async Task EntraExecutor_CacheAdministration_StaleDeleteCannotClearReplacementOrReleaseMemory()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        ICredentialCacheAdministration administration = fixture.Executor;
        var oldDescriptor = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        var replacement = await CommitSessionAsync(fixture.Session, fixture.Request);
        var currentDescriptor = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(oldDescriptor.Identifier, currentDescriptor.Identifier);
        Assert.Equal(replacement, currentDescriptor.Generation);
        var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(async () =>
            await administration.DeleteAsync(oldDescriptor.Identifier, TestContext.Current.CancellationToken));
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, failure.Failure);
        Assert.True(await fixture.Session.IsCurrentGenerationAsync(replacement, TestContext.Current.CancellationToken));
        await using var current = Assert.IsType<AccessSucceeded<EntraCredentialAccessResult>>(
            await fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken)).Result;
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await current.AccessToken.UseAsync((material, _) => ValueTask.FromResult(material.Length), TestContext.Current.CancellationToken));
        await administration.DeleteAsync(currentDescriptor.Identifier, TestContext.Current.CancellationToken);
        Assert.Empty(await administration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(
            await current.AccessToken.UseAsync<int>(
                (_, _) => throw new InvalidOperationException("Deleted cache generation cannot be used."),
                TestContext.Current.CancellationToken));
        Assert.IsType<EntraGenerationUseUnavailable<int>>(await fixture.Session.UseGenerationAsync<int>(
            replacement, (_, _) => throw new InvalidOperationException("Deleted private generation cannot be used."),
            TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Acquirer.Calls);
    }

    [Fact]
    public async Task EntraExecutor_DeletedCacheLeaseRenewal_PreservesFinalizedAccount()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        ICredentialCacheAdministration administration = fixture.Executor;
        var descriptor = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        var approvedAccount = fixture.Acquirer.Account;
        await administration.DeleteAsync(descriptor.Identifier, TestContext.Current.CancellationToken);
        var required = Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(
            await result.AccessToken.UseAsync<int>(
                (_, _) => throw new InvalidOperationException("Deleted material cannot run."),
                TestContext.Current.CancellationToken));
        var row = fixture.Registry.GetSnapshot().Single(snapshot => snapshot.Identifier == required.RequestIdentifier);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        fixture.Acquirer.Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "another-account");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Registry.Changed += (_, _) =>
        {
            if (fixture.Registry.GetSnapshot().Any(snapshot =>
                snapshot.Identifier == required.RequestIdentifier && snapshot.State == UserAuthorizationState.Failed))
            {
                failed.TrySetResult();
            }
        };
        await fixture.ApplyAsync(row);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(await fixture.Session.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(UserAuthorizationState.Failed,
            fixture.Registry.GetSnapshot().Single(snapshot => snapshot.Identifier == required.RequestIdentifier).State);
        Assert.All(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken),
            memory => Assert.Equal(approvedAccount, memory.AccountIdentifier));
        Assert.Equal(2, fixture.Acquirer.Calls);
    }

    [Fact]
    public async Task EntraExecutor_CacheAdministration_CancelledUnknownAndDisposedDeletesAreSafe()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync(ReleaseMemoryScopeKind.AllUses);
        ICredentialCacheAdministration administration = fixture.Executor;
        var descriptor = Assert.Single(await administration.ListAsync(TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await administration.DeleteAsync(descriptor.Identifier, cancellation.Token));
        Assert.True(await fixture.Session.IsCurrentGenerationAsync(descriptor.Generation, TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<CredentialAdministrationException>(async () =>
            await administration.DeleteAsync(new() { Value = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        Assert.Equal(CredentialAdministrationFailure.Unavailable, missing.Failure);
        await fixture.Executor.DisposeAsync();
        var disposed = await Assert.ThrowsAsync<CredentialAdministrationException>(async () =>
            await administration.DeleteAsync(descriptor.Identifier, TestContext.Current.CancellationToken));
        Assert.Equal(CredentialAdministrationFailure.Unavailable, disposed.Failure);
    }

    private static async Task<CredentialMaterialGenerationIdentifier> CommitSessionAsync(
        IEntraCredentialSession session,
        EntraCredentialAccessRequest request)
    {
        var inspection = await session.InspectCacheAsync(request, TestContext.Current.CancellationToken);
        var permit = new ProviderInteractionPermit(
            new() { Value = Guid.NewGuid() }, 1, CredentialExecutorKind.Entra, Guid.NewGuid(),
            inspection.AcquisitionKey, () => true);
        using var gate = new SemaphoreSlim(1, 1);
        var context = new ProviderAcquisitionCommitContext(
            () => true, TestContext.Current.CancellationToken, gate, permit.BindExecution(() => true));
        return Assert.IsType<ProviderAcquisitionSucceeded>(
            await session.ExecuteAsync(request, context, TestContext.Current.CancellationToken)).CommittedGeneration;
    }

    private sealed class TrackingCoordinator : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int participants;

        internal TaskCompletionSource FollowerJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var pending = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (Interlocked.Increment(ref this.participants) == 2)
            {
                this.FollowerJoined.TrySetResult();
            }

            return pending;
        }

        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    private sealed class TrackingPolicy(ReleaseAuthorizationPolicy policy) : IReleaseAuthorizationPolicy, IAsyncDisposable
    {
        internal int Commits { get; private set; }
        internal int Finalizations { get; private set; }

        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.EvaluateAsync(request, cancellationToken);
        public async ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken = default)
        {
            var result = await policy.CommitAsync(decision, cancellationToken);
            this.Commits++;
            return result;
        }

        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
            ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.ValidateAsync(identifier, request, cancellationToken);
        public async ValueTask FinalizeCredentialIdentityAsync(
            ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken = default)
        {
            await policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
            this.Finalizations++;
        }

        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) => policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) => policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => policy.DisposeAsync();
    }

    private static EntraCredentialAccessRequest CreateRequest()
    {
        var policyRequest = ReleaseAuthorizationContractTests.Request();
        var request = new EntraCredentialAccessRequest
        {
            Authority = new() { Value = new Uri("https://login.microsoftonline.com/organizations/v2.0") },
            Client = new() { Value = "test-client" },
            CachePartition = new() { Value = "test-partition" },
            Scopes = [new() { Value = "api://example/.default" }],
            Credential = policyRequest.Credential with { Provider = CredentialProviderKind.Entra, Kind = CredentialKind.AccessToken },
            Requestor = new()
            {
                Identifier = policyRequest.RequestorIdentifier,
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            },
            Use = policyRequest.Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.AllUses] },
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = [],
        };
        return request with
        {
            Credential = request.Credential with { LogicalIdentifier = request.CreateLogicalIdentifier(), AccountIdentifier = null },
            Sources = [new()
            {
                Identifier = request.CreateSourceIdentifier(),
                Kind = CredentialSourceKind.ProviderCache,
                Provider = CredentialProviderKind.Entra,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = "Microsoft Entra",
            }],
        };
    }

    private sealed class Acquirer : IEntraTokenAcquirer
    {
        internal int Calls { get; private set; }
        internal Exception? Failure { get; set; }
        internal DateTimeOffset ExpiresOn { get; set; }
        internal CredentialAccountIdentifier Account { get; set; } =
            CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "stable-test-account");
        internal Func<CancellationToken, ValueTask>? BeforeAcquire { get; set; }

        public async ValueTask<EntraAcquiredToken> AcquireAsync(CancellationToken cancellationToken)
        {
            this.Calls++;
            if (this.BeforeAcquire is not null)
            {
                await this.BeforeAcquire(cancellationToken);
            }

            if (this.Failure is not null)
            {
                throw this.Failure;
            }

            return new() { Token = new("private-token", this.ExpiresOn), AccountIdentifier = this.Account };
        }
    }

    private sealed class ForbiddenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected Azure Identity call.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected Azure Identity call.");
    }
}
