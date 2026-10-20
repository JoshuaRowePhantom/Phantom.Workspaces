using System.Security;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class McpOAuthCacheCommitCoordinatorTests
{
    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_Store_CompletesBeforePublish()
    {
        var store = new MemoryStore { BlockWrite = true };
        var cache = Cache(store);
        var operation = new Operation();
        var context = Context(operation);
        await using var coordinator = new McpOAuthCacheCommitCoordinator(cache, operation.CompatibilityKey);
        var generation = await coordinator.BeginGenerationAsync(context, TestContext.Current.CancellationToken);
        var waiting = coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        var storing = coordinator.StoreAndPublishAsync(Tokens("committed"), generation, TestContext.Current.CancellationToken).AsTask();
        await store.Writing.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.Null(context.CommittedGeneration);
        Assert.Equal(0, store.Reads);
        store.FinishWrite.TrySetResult();
        await storing;
        await waiting;

        var freshCache = Cache(store);
        var metadata = await ((ICredentialGenerationCache)freshCache).InspectGenerationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(generation.MaterialGeneration, metadata!.Generation);
        Assert.Equal(generation.MaterialGeneration, context.CommittedGeneration);
        Assert.Equal(generation.MaterialGeneration, Assert.IsType<ProviderAcquisitionSucceeded>(generation.Outcome).CommittedGeneration);
        Assert.Equal(0, store.Reads);
        var tokens = await freshCache.GetGenerationTokensAsync(metadata.Generation, TestContext.Current.CancellationToken);
        Assert.Equal("committed", tokens!.AccessToken);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_Refresh_PublishesOnlyNewAuthoritativeGeneration()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(cache, operation.CompatibilityKey);
        var original = await coordinator.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        await coordinator.StoreAndPublishAsync(Tokens("initial"), original, TestContext.Current.CancellationToken);
        var refreshContext = Context(operation);
        var refresh = await coordinator.BeginGenerationAsync(refreshContext, TestContext.Current.CancellationToken);
        store.BlockWrite = true;
        var follower = coordinator.WaitForCommitAsync(refresh, TestContext.Current.CancellationToken);
        var writing = coordinator.StoreAndPublishAsync(Tokens("refreshed"), refresh, TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(2, store.Writes);
        Assert.False(follower.IsCompleted);
        Assert.Null(refreshContext.CommittedGeneration);
        store.FinishWrite.TrySetResult();
        await writing;
        await follower;

        var freshCache = Cache(store);
        var generations = (ICredentialGenerationCache)freshCache;
        Assert.NotEqual(original.MaterialGeneration, refresh.MaterialGeneration);
        Assert.False(await generations.IsCurrentGenerationAsync(original.MaterialGeneration, TestContext.Current.CancellationToken));
        Assert.True(await generations.IsCurrentGenerationAsync(refresh.MaterialGeneration, TestContext.Current.CancellationToken));
        Assert.Null(await freshCache.GetGenerationTokensAsync(original.MaterialGeneration, TestContext.Current.CancellationToken));
        Assert.Equal("refreshed", (await freshCache.GetGenerationTokensAsync(
            refresh.MaterialGeneration, TestContext.Current.CancellationToken))!.AccessToken);
        Assert.Equal(refresh.MaterialGeneration, refreshContext.CommittedGeneration);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_OldGeneration_CannotSignalCurrent()
    {
        var store = new MemoryStore();
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var old = await coordinator.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        var oldWait = coordinator.WaitForCommitAsync(old, TestContext.Current.CancellationToken);
        var current = await coordinator.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        var currentWait = coordinator.WaitForCommitAsync(current, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.StoreAndPublishAsync(Tokens("old"), old, TestContext.Current.CancellationToken));
        Assert.False(currentWait.IsCompleted);
        Assert.Equal(0, store.Writes);
        await coordinator.StoreAndPublishAsync(Tokens("current"), current, TestContext.Current.CancellationToken);
        await currentWait;
        Assert.Equal("current", (await Cache(store).GetTokensAsync(TestContext.Current.CancellationToken))!.AccessToken);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_SeparateCacheOwners_CannotOverwriteNewerReservation()
    {
        var store = new MemoryStore();
        var operation = new Operation();
        await using var first = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        await using var second = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var oldContext = Context(operation);
        var old = await first.BeginGenerationAsync(oldContext, TestContext.Current.CancellationToken);
        var oldWait = first.WaitForCommitAsync(old, TestContext.Current.CancellationToken);
        var current = await second.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        await second.StoreAndPublishAsync(Tokens("current"), current, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.StoreAndPublishAsync(Tokens("old"), old, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldWait);
        Assert.Equal(AccessErrorCode.InvalidGeneration, Assert.IsType<ProviderAcquisitionFailed>(old.Outcome).ErrorCode);
        Assert.Null(oldContext.CommittedGeneration);
        Assert.Equal(1, store.Writes);
        Assert.Equal("current", (await Cache(store).GetGenerationTokensAsync(
            current.MaterialGeneration, TestContext.Current.CancellationToken))!.AccessToken);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_CancelledStore_UnblocksFollowersWithoutWriting()
    {
        var store = new MemoryStore();
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var generation = await coordinator.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        var follower = coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.StoreAndPublishAsync(Tokens("late"), generation, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        Assert.IsType<ProviderAcquisitionCancelled>(generation.Outcome);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_DisposeDuringWrite_DrainsAuthoritativeCommit()
    {
        var store = new MemoryStore { BlockWrite = true };
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var context = Context(operation);
        var generation = await coordinator.BeginGenerationAsync(context, TestContext.Current.CancellationToken);
        var follower = coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        var writing = coordinator.StoreAndPublishAsync(Tokens("committed"), generation, TestContext.Current.CancellationToken).AsTask();
        await store.Writing.Task.WaitAsync(TestContext.Current.CancellationToken);
        var shutdown = coordinator.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.False(follower.IsCompleted);
        store.FinishWrite.TrySetResult();
        await writing;
        await shutdown;
        await follower;
        Assert.Equal(generation.MaterialGeneration, context.CommittedGeneration);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_CancelledFollower_DoesNotCancelStoreLeader()
    {
        var store = new MemoryStore { BlockWrite = true };
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var generation = await coordinator.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        var storing = coordinator.StoreAndPublishAsync(Tokens("current"), generation, TestContext.Current.CancellationToken).AsTask();
        await store.Writing.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var follower = coordinator.WaitForCommitAsync(generation, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        Assert.False(storing.IsCompleted);
        store.FinishWrite.TrySetResult();
        await storing;
        await coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        Assert.IsType<ProviderAcquisitionSucceeded>(generation.Outcome);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_FailedWrite_DoesNotPublishOrRetainOldGeneration()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        await cache.StoreTokensAsync(Tokens("old"), TestContext.Current.CancellationToken);
        var generationCache = (ICredentialGenerationCache)cache;
        var old = await generationCache.InspectGenerationAsync(TestContext.Current.CancellationToken);
        store.FailWrite = true;
        var operation = new Operation();
        var context = Context(operation);
        await using var coordinator = new McpOAuthCacheCommitCoordinator(cache, operation.CompatibilityKey);
        var generation = await coordinator.BeginGenerationAsync(context, TestContext.Current.CancellationToken);
        var follower = coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        var leaderFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.StoreAndPublishAsync(Tokens("uncommitted"), generation, TestContext.Current.CancellationToken));
        var followerFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => follower);
        Assert.Equal("OAuth cache commit failed.", leaderFailure.Message);
        Assert.Equal(leaderFailure.Message, followerFailure.Message);
        Assert.Null(leaderFailure.InnerException);
        Assert.Null(followerFailure.InnerException);
        Assert.Equal(AccessErrorCode.PersistenceFailure, Assert.IsType<ProviderAcquisitionFailed>(generation.Outcome).ErrorCode);
        Assert.Null(context.CommittedGeneration);
        Assert.Null(await generationCache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.False(await generationCache.IsCurrentGenerationAsync(old!.Generation, TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetGenerationTokensAsync(old.Generation, TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_TombstonedContext_CannotWriteOrPublish()
    {
        var store = new MemoryStore();
        var operation = new Operation();
        var active = true;
        var context = Context(operation, () => active);
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var generation = await coordinator.BeginGenerationAsync(context, TestContext.Current.CancellationToken);
        var follower = coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        active = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.StoreAndPublishAsync(Tokens("late"), generation, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        Assert.Equal(0, store.Writes);
        Assert.Null(context.CommittedGeneration);
        Assert.IsType<ProviderAcquisitionCancelled>(generation.Outcome);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_Dispose_TombstonesPendingPublication()
    {
        var store = new MemoryStore();
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var generation = await coordinator.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken);
        var follower = coordinator.WaitForCommitAsync(generation, TestContext.Current.CancellationToken);
        await coordinator.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.StoreAndPublishAsync(Tokens("late"), generation, TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_RequiresMatchingActiveMcpProof()
    {
        var store = new MemoryStore();
        var operation = new Operation();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(Cache(store), operation.CompatibilityKey);
        var rawPermit = Permit(operation);
        var context = new ProviderAcquisitionCommitContext(() => true, CancellationToken.None, new(1, 1), rawPermit);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.BeginGenerationAsync(context, TestContext.Current.CancellationToken));
        var otherKey = operation.CompatibilityKey with { Source = new() { Digest = new string('f', 64) } };
        await using var other = new McpOAuthCacheCommitCoordinator(Cache(store), otherKey);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await other.BeginGenerationAsync(Context(operation), TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task McpOAuthCacheCommitCoordinator_ApprovedFollowers_ReconnectOnlyFromCommittedCache()
    {
        var store = new MemoryStore { BlockWrite = true };
        var cache = Cache(store);
        var leader = new Operation();
        var follower = new Operation();
        await using var acquisitions = new ProviderAcquisitionCoordinator();
        await using var coordinator = new McpOAuthCacheCommitCoordinator(cache, leader.CompatibilityKey);
        leader.Execute = async (context, cancellationToken) =>
        {
            var generation = await coordinator.BeginGenerationAsync(context, cancellationToken);
            await coordinator.StoreAndPublishAsync(Tokens("committed"), generation, cancellationToken);
            return generation.Outcome!;
        };
        follower.Execute = (_, _) => throw new InvalidOperationException("Follower cannot reuse the leader callback.");
        var leaderTask = acquisitions.ExecuteOrJoinAsync(leader, Permit(leader), TestContext.Current.CancellationToken).AsTask();
        await store.Writing.Task.WaitAsync(TestContext.Current.CancellationToken);
        var followerTask = acquisitions.ExecuteOrJoinAsync(follower, Permit(follower), TestContext.Current.CancellationToken).AsTask();
        Assert.False(followerTask.IsCompleted);
        Assert.NotEqual(leader.RequestIdentifier, follower.RequestIdentifier);
        store.FinishWrite.TrySetResult();
        var results = await Task.WhenAll(leaderTask, followerTask);
        var receipt = Assert.IsType<ProviderAcquisitionSucceeded>(results[1]);
        Assert.Equal(Assert.IsType<ProviderAcquisitionSucceeded>(results[0]).CommittedGeneration, receipt.CommittedGeneration);
        Assert.Equal("committed", (await Cache(store).GetGenerationTokensAsync(
            receipt.CommittedGeneration, TestContext.Current.CancellationToken))!.AccessToken);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task CredentialManagerTokenCache_AuthoritativeRecord_RejectsExternallyRotatedGeneration()
    {
        var firstStore = new MemoryStore();
        var firstCache = Cache(firstStore);
        var firstGenerationCache = (ICredentialGenerationCache)firstCache;
        await firstCache.StoreTokensAsync(Tokens("old"), TestContext.Current.CancellationToken);
        var old = await firstGenerationCache.InspectGenerationAsync(TestContext.Current.CancellationToken);
        var externalStore = new MemoryStore(firstStore);
        var externalCache = Cache(externalStore);
        Assert.Null(await ((ICredentialGenerationCache)externalCache).InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, externalStore.Reads);
        await externalCache.StoreTokensAsync(Tokens("external"), TestContext.Current.CancellationToken);
        var current = await ((ICredentialGenerationCache)externalCache).InspectGenerationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await firstGenerationCache.InvalidateGenerationAsync(old!.Generation, TestContext.Current.CancellationToken));
        Assert.Equal(0, firstStore.Deletes);
        Assert.False(await firstGenerationCache.IsCurrentGenerationAsync(old.Generation, TestContext.Current.CancellationToken));
        Assert.Null(await firstCache.GetGenerationTokensAsync(old.Generation, TestContext.Current.CancellationToken));
        Assert.Equal("external", (await firstCache.GetGenerationTokensAsync(
            current!.Generation, TestContext.Current.CancellationToken))!.AccessToken);

        var coldStore = new MemoryStore(firstStore);
        var coldCache = Cache(coldStore);
        Assert.Null(await ((ICredentialGenerationCache)coldCache).InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, coldStore.Reads);
        Assert.NotNull(await coldCache.GetTokensAsync(TestContext.Current.CancellationToken));
        Assert.Equal(current.Generation, (await ((ICredentialGenerationCache)coldCache)
            .InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation);
    }

    [Fact]
    public async Task CredentialManagerTokenCache_MetadataAndInvalidation_ArePartitionAndGenerationBound()
    {
        var store = new MemoryStore();
        var cache = Cache(store);
        var generationCache = (ICredentialGenerationCache)cache;
        Assert.Null(await generationCache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        await cache.StoreTokensAsync(Tokens("old"), TestContext.Current.CancellationToken);
        var old = await generationCache.InspectGenerationAsync(TestContext.Current.CancellationToken);
        await cache.StoreTokensAsync(Tokens("new"), TestContext.Current.CancellationToken);
        var freshGenerationCache = (ICredentialGenerationCache)Cache(store);
        var current = await freshGenerationCache.InspectGenerationAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(old!.Generation, current!.Generation);
        Assert.NotNull(current.ExpiresOn);
        Assert.Equal(0, store.Reads);
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await freshGenerationCache.InvalidateGenerationAsync(old.Generation, TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Deletes);
        Assert.Null(await cache.GetGenerationTokensAsync(old.Generation, TestContext.Current.CancellationToken));
        Assert.Equal("new", (await cache.GetGenerationTokensAsync(current.Generation, TestContext.Current.CancellationToken))!.AccessToken);
        Assert.Null(await ((ICredentialGenerationCache)new CredentialManagerTokenCache(store, "mcp-oauth:", "other"))
            .InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Null(await ((ICredentialGenerationCache)Cache(new MemoryStore()))
            .InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await freshGenerationCache.InvalidateGenerationAsync(current.Generation, TestContext.Current.CancellationToken));
        Assert.Equal(1, store.Deletes);
        Assert.Null(await cache.GetTokensAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CredentialGenerationInvalidationOutcome.Missing,
            await freshGenerationCache.InvalidateGenerationAsync(current.Generation, TestContext.Current.CancellationToken));
    }

    private static CredentialManagerTokenCache Cache(MemoryStore store) => new(store, "mcp-oauth:", "private-server");

    private static TokenContainer Tokens(string value) => new()
    {
        TokenType = "Bearer",
        AccessToken = value,
        RefreshToken = "private-refresh",
        ExpiresIn = 3600,
        ObtainedAt = DateTimeOffset.UtcNow,
    };

    private static ProviderInteractionPermit Permit(Operation operation) => new(
        operation.RequestIdentifier, operation.Revision, operation.ExecutorKind,
        operation.AcquisitionGeneration, operation.CompatibilityKey, () => true);

    private static ProviderAcquisitionCommitContext Context(Operation operation, Func<bool>? active = null) => new(
        active ?? (() => true), CancellationToken.None, new(1, 1), Permit(operation).BindExecution(active ?? (() => true)));

    private sealed class Operation : IProviderAcquisitionOperation
    {
        public Func<ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>>? Execute { get; set; }
        public ProviderAcquisitionKey CompatibilityKey { get; } = new()
        {
            Configuration = new() { Digest = new string('a', 64) },
            Source = new() { Digest = new string('b', 64) },
            Protocol = ProviderProtocolKind.OAuth,
        };
        public UserAuthorizationRequestIdentifier RequestIdentifier { get; } = new() { Value = Guid.NewGuid() };
        public long Revision => 1;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.McpOAuth;
        public Guid AcquisitionGeneration { get; } = Guid.NewGuid();
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => this.Execute!(commitContext, cancellationToken);
    }

    private sealed class MemoryStore : IPlatformSecretStore
    {
        private readonly Dictionary<string, SecureString> secrets;

        public MemoryStore(MemoryStore? shared = null)
        {
            this.secrets = shared?.secrets ?? [];
        }

        public bool BlockWrite { get; set; }
        public bool FailWrite { get; set; }
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public int Deletes { get; private set; }

        public async Task WriteAsync(string name, SecureString value, CancellationToken cancellationToken)
        {
            this.Writes++;
            this.Writing.TrySetResult();
            if (this.BlockWrite)
            {
                await this.FinishWrite.Task.WaitAsync(cancellationToken);
            }
            if (this.FailWrite)
            {
                throw new IOException("private-token private-server unsafe-path");
            }

            this.secrets[name] = value.Copy();
        }

        public Task<SecureString?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(this.secrets.TryGetValue(name, out var value) ? value.Copy() : null);
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            this.Deletes++;
            this.secrets.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OAuth metadata must not enumerate secret values.");
    }
}
