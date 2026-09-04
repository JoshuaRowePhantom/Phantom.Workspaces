using System.Collections.Concurrent;
using Phantom.Workspaces;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrokerQuerySatisfierTests
{
    // ---- Issuance-time subsumption ---------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_MultiIdGetSupersetSatisfiesSubsetGet_NoSecondCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var broadTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1), Id(2), Id(3)), ct);
        var broadCall = await executor.WaitForCallAsync(QueryKindGet, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(2)), ct);

        broadCall.Complete(GetResultFor(Id(1), Id(2), Id(3)));

        var narrow = await narrowTask.WaitAsync(ct);
        await broadTask.WaitAsync(ct);

        Assert.Equal(1, executor.GetCallCount);
        Assert.Equal(new[] { Id(2) }, Ids(narrow).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_FullScopeGetChangedSatisfiesNarrowGet_NoSecondCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var changedTask = satisfier.SatisfyOrIssueOnDemandGetChangedAsync(GetChangedFor(Id(1), Id(2), Id(3)), ct);
        var changedCall = await executor.WaitForCallAsync(QueryKindGetChanged, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(2)), ct);

        changedCall.Complete(GetChangedResultFor(Snapshot(Id(2))));

        var narrow = await narrowTask.WaitAsync(ct);
        await changedTask.WaitAsync(ct);

        Assert.Equal(0, executor.GetCallCount);
        Assert.Equal(new[] { Id(2) }, Ids(narrow).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_NarrowGetWithIdNotCoveredByBroad_IssuesOwnCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var broadTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1), Id(2), Id(3)), ct);
        await executor.WaitForCallAsync(QueryKindGet, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(9)), ct);
        var narrowCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        narrowCall.Complete(GetResultFor(Id(9)));

        var narrow = await narrowTask.WaitAsync(ct);
        Assert.Equal(new[] { Id(9) }, Ids(narrow).ToArray());

        executor.CompleteAll(GetResultFor(Id(1), Id(2), Id(3)));
        await broadTask.WaitAsync(ct);
        Assert.Equal(2, executor.GetCallCount);
    }

    // ---- Result-time subsumption -----------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_BroadQueryResultsCoverNarrowGetId_ResultTimeSubsumes()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var queryTask = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("all"), ct);
        var queryCall = await executor.WaitForCallAsync(QueryKindQuery, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(5)), ct);

        queryCall.Complete(QueryResultFor(Id(4), Id(5), Id(6)));

        var narrow = await narrowTask.WaitAsync(ct);
        await queryTask.WaitAsync(ct);

        Assert.Equal(0, executor.GetCallCount);
        Assert.Equal(new[] { Id(5) }, Ids(narrow).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_BroadQueryResultsDoNotCoverNarrowGetId_NarrowIssuesFallthrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var queryTask = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("all"), ct);
        var queryCall = await executor.WaitForCallAsync(QueryKindQuery, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(5)), ct);

        // Results do not include id 5, so the narrow get must fall through.
        queryCall.Complete(QueryResultFor(Id(4), Id(6)));

        var narrowCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        narrowCall.Complete(GetResultFor(Id(5)));

        var narrow = await narrowTask.WaitAsync(ct);
        await queryTask.WaitAsync(ct);

        Assert.Equal(1, executor.GetCallCount);
        Assert.Equal(new[] { Id(5) }, Ids(narrow).ToArray());
    }

    // ---- Dedup -----------------------------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_TwoConcurrentIdenticalGetRequests_IssuesOneUnderlyingCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var first = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);
        var second = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);

        var call = await executor.WaitForCallAsync(QueryKindGet, ct);
        call.Complete(GetResultFor(Id(1)));

        var r1 = await first.WaitAsync(ct);
        var r2 = await second.WaitAsync(ct);

        Assert.Equal(1, executor.GetCallCount);
        Assert.Same(r1, r2);
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_DuplicateAfterFirstCompletes_IssuesFreshUnderlyingCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var first = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);
        var call1 = await executor.WaitForCallAsync(QueryKindGet, ct);
        call1.Complete(GetResultFor(Id(1)));
        await first.WaitAsync(ct);

        var second = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);
        var call2 = await executor.WaitForCallAsync(QueryKindGet, ct);
        call2.Complete(GetResultFor(Id(1)));
        await second.WaitAsync(ct);

        Assert.Equal(2, executor.GetCallCount);
    }

    // ---- On-demand parallelism -------------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_ThreeDistinctOnDemandQueries_ExecuteConcurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var t1 = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("a"), ct);
        var t2 = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("b"), ct);
        var t3 = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("c"), ct);

        var c1 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        var c2 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        var c3 = await executor.WaitForCallAsync(QueryKindQuery, ct);

        Assert.Equal(3, executor.MaxConcurrency);

        c1.Complete(QueryResultFor());
        c2.Complete(QueryResultFor());
        c3.Complete(QueryResultFor());
        await Task.WhenAll(t1, t2, t3).WaitAsync(ct);
    }

    // ---- Periodic throttle -----------------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_ThreeDistinctPeriodicQueries_ObserveAtMostOneConcurrentCall_InFifoOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var t1 = satisfier.SatisfyOrEnqueuePeriodicQueryAsync(Query("a"), ct);
        var t2 = satisfier.SatisfyOrEnqueuePeriodicQueryAsync(Query("b"), ct);
        var t3 = satisfier.SatisfyOrEnqueuePeriodicQueryAsync(Query("c"), ct);

        var c1 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        Assert.Equal(1, executor.CallCount);
        c1.Complete(QueryResultFor());
        await t1.WaitAsync(ct);

        var c2 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        c2.Complete(QueryResultFor());
        await t2.WaitAsync(ct);

        var c3 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        c3.Complete(QueryResultFor());
        await t3.WaitAsync(ct);

        Assert.Equal(1, executor.MaxConcurrency);
        Assert.Equal(new[] { "a", "b", "c" }, executor.QueryOrder);
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_PeriodicHoldingSlot_OnDemandQueriesRunConcurrentlyBesideIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var periodic = satisfier.SatisfyOrEnqueuePeriodicQueryAsync(Query("p"), ct);
        var periodicCall = await executor.WaitForCallAsync(QueryKindQuery, ct);

        var d1 = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("d1"), ct);
        var d2 = satisfier.SatisfyOrIssueOnDemandQueryAsync(Query("d2"), ct);
        var dc1 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        var dc2 = await executor.WaitForCallAsync(QueryKindQuery, ct);

        // The periodic call plus both on-demand calls are all in flight at once.
        Assert.Equal(3, executor.MaxConcurrency);

        periodicCall.Complete(QueryResultFor());
        dc1.Complete(QueryResultFor());
        dc2.Complete(QueryResultFor());
        await Task.WhenAll(periodic, d1, d2).WaitAsync(ct);
    }

    // ---- Cross-trigger ---------------------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_OnDemandGetSatisfyingPendingPeriodicGet_ImmediatelyRefreshesPeriodic()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        // Occupy the single periodic slot with an unrelated in-flight periodic get.
        var blocker = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(9)), ct);
        var blockerCall = await executor.WaitForCallAsync(QueryKindGet, ct);

        // Periodic get for {b} queues behind the blocker (loop is busy).
        var periodicGet = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(2)), ct);

        // On-demand broad get that covers {b}.
        var onDemand = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1), Id(2), Id(3)), ct);
        var onDemandCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        onDemandCall.Complete(GetResultFor(Id(1), Id(2), Id(3)));

        // The periodic get is refreshed from the on-demand result immediately.
        var periodic = await periodicGet.WaitAsync(ct);
        Assert.Equal(new[] { Id(2) }, Ids(periodic).ToArray());

        blockerCall.Complete(GetResultFor(Id(9)));
        await Task.WhenAll(blocker, onDemand).WaitAsync(ct);

        // The periodic {b} was served from the on-demand broad get: it never round-tripped on its own.
        Assert.False(executor.AnyGetForExactly(Id(2)));
    }

    // ---- Skip-refresh ----------------------------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_PeriodicDueForRefreshWhileSubsumerLive_SkipsAndIsServedBySubsumer()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var broad = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(1), Id(2)), ct);
        var broadCall = await executor.WaitForCallAsync(QueryKindGet, ct);

        // Narrow periodic get issued while the broad periodic get is live.
        var narrow = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(1)), ct);

        broadCall.Complete(GetResultFor(Id(1), Id(2)));

        var narrowResult = await narrow.WaitAsync(ct);
        await broad.WaitAsync(ct);

        Assert.Equal(1, executor.GetCallCount);
        Assert.Equal(new[] { Id(1) }, Ids(narrowResult).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_SubsumerFaultsDuringBRefreshCycle_BFallsThroughToOwnCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var broadTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1), Id(2)), ct);
        var broadCall = await executor.WaitForCallAsync(QueryKindGet, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);

        broadCall.Fault(new InvalidOperationException("boom"));

        // Broad faulted, so the narrow get falls through to its own call.
        var narrowCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        narrowCall.Complete(GetResultFor(Id(1)));

        var narrow = await narrowTask.WaitAsync(ct);
        Assert.Equal(new[] { Id(1) }, Ids(narrow).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => broadTask);
    }

    // ---- Cancellation / faults / lifecycle -------------------------------

    [Fact]
    public async Task EntityBrokerQuerySatisfier_OneOfTwoDedupedAwaitersCancelled_UnderlyingCallStillCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        // Block the periodic slot so both deduped awaiters attach before the get executes.
        var blocker = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(9)), ct);
        var blockerCall = await executor.WaitForCallAsync(QueryKindGet, ct);

        using var cts = new CancellationTokenSource();
        var first = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(1)), cts.Token);
        var second = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(1)), ct);

        blockerCall.Complete(GetResultFor(Id(9)));
        await blocker.WaitAsync(ct);

        var call = await executor.WaitForCallAsync(QueryKindGet, ct);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        call.Complete(GetResultFor(Id(1)));
        var r2 = await second.WaitAsync(ct);
        Assert.Equal(new[] { Id(1) }, Ids(r2).ToArray());
        Assert.False(call.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_AllDedupedAwaitersCancelled_UnderlyingCallLinkedTokenIsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var blocker = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(9)), ct);
        var blockerCall = await executor.WaitForCallAsync(QueryKindGet, ct);

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var first = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(1)), cts1.Token);
        var second = satisfier.SatisfyOrEnqueuePeriodicGetAsync(GetFor(Id(1)), cts2.Token);

        blockerCall.Complete(GetResultFor(Id(9)));
        await blocker.WaitAsync(ct);

        var call = await executor.WaitForCallAsync(QueryKindGet, ct);

        cts1.Cancel();
        cts2.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        await call.WaitCancelledAsync(ct);
        Assert.True(call.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_UnderlyingCallThrows_FaultsAwaiterAndPeriodicLoopContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var t1 = satisfier.SatisfyOrEnqueuePeriodicQueryAsync(Query("a"), ct);
        var c1 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        c1.Fault(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t1);

        var t2 = satisfier.SatisfyOrEnqueuePeriodicQueryAsync(Query("b"), ct);
        var c2 = await executor.WaitForCallAsync(QueryKindQuery, ct);
        c2.Complete(QueryResultFor());
        await t2.WaitAsync(ct);
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_UnderlyingBroadCallThrows_FaultsBroadAndFallsThroughSliceAwaiters()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor);

        var broadTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1), Id(2)), ct);
        var broadCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(2)), ct);

        broadCall.Fault(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => broadTask);

        var narrowCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        narrowCall.Complete(GetResultFor(Id(2)));
        var narrow = await narrowTask.WaitAsync(ct);
        Assert.Equal(new[] { Id(2) }, Ids(narrow).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_GetChangedNotChangedMarkerForSubsumedNarrowId_ResolvesFromCachedSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        var cached = Snapshot(Id(2));
        await using var satisfier = new EntityBrokerQuerySatisfier(
            executor,
            id => id == Id(2) ? cached : null);

        var changedTask = satisfier.SatisfyOrIssueOnDemandGetChangedAsync(GetChangedFor(Id(1), Id(2)), ct);
        var changedCall = await executor.WaitForCallAsync(QueryKindGetChanged, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(2)), ct);

        // id 2 is not in the changed set (not-changed) -> resolved from cache.
        changedCall.Complete(GetChangedResultFor(Snapshot(Id(1))));

        var narrow = await narrowTask.WaitAsync(ct);
        await changedTask.WaitAsync(ct);

        Assert.Equal(0, executor.GetCallCount);
        Assert.Equal(new[] { Id(2) }, Ids(narrow).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_GetChangedNotChangedMarkerAndCacheMisses_FallsThroughToFreshNarrowCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        await using var satisfier = new EntityBrokerQuerySatisfier(executor, _ => null);

        var changedTask = satisfier.SatisfyOrIssueOnDemandGetChangedAsync(GetChangedFor(Id(1), Id(2)), ct);
        var changedCall = await executor.WaitForCallAsync(QueryKindGetChanged, ct);

        var narrowTask = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(2)), ct);

        changedCall.Complete(GetChangedResultFor(Snapshot(Id(1))));

        var narrowCall = await executor.WaitForCallAsync(QueryKindGet, ct);
        narrowCall.Complete(GetResultFor(Id(2)));

        var narrow = await narrowTask.WaitAsync(ct);
        await changedTask.WaitAsync(ct);

        Assert.Equal(1, executor.GetCallCount);
        Assert.Equal(new[] { Id(2) }, Ids(narrow).ToArray());
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_DisposeAsync_CancelsOutstandingAwaiters()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        var satisfier = new EntityBrokerQuerySatisfier(executor);

        var pending = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);
        await executor.WaitForCallAsync(QueryKindGet, ct);

        await satisfier.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task EntityBrokerQuerySatisfier_EnqueueAfterDispose_ReturnsCanceledTask()
    {
        var ct = TestContext.Current.CancellationToken;
        var executor = new ControllableExecutor();
        var satisfier = new EntityBrokerQuerySatisfier(executor);
        await satisfier.DisposeAsync();

        var task = satisfier.SatisfyOrIssueOnDemandGetAsync(GetFor(Id(1)), ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, executor.GetCallCount);
    }

    // ---- Helpers ---------------------------------------------------------

    private const int QueryKindGet = 0;
    private const int QueryKindQuery = 1;
    private const int QueryKindGetChanged = 2;

    private static EntityId Id(int n) => new($"{n:D8}-0000-0000-0000-000000000000");

    private static Timestamp Ts() => new(DateTimeOffset.UnixEpoch, "1");

    private static EntitySnapshot Snapshot(EntityId id) => new()
    {
        EntityId = id,
        ModifiedTime = Ts(),
        Relationships = [],
    };

    private static QueryEntitySnapshot QuerySnapshot(EntityId id) => new()
    {
        EntityId = id,
        ModifiedTime = Ts(),
        Relationships = [],
        MatchingClauseIdentifiers = [],
    };

    private static GetRequest GetFor(params EntityId[] ids) => new()
    {
        Entities = ids.Select(static id => new GetEntityRequest { EntityId = id }).ToArray(),
    };

    private static GetChangedEntitiesRequest GetChangedFor(params EntityId[] ids) => new()
    {
        EntityIdTimestamps = ids.Select(static id => new EntityIdTimestamp(id, Ts())).ToArray(),
    };

    private static QueryRequest Query(string identifier) => new()
    {
        Clauses = new[]
        {
            new TopLevelQueryClause
            {
                ClauseIdentifier = new QueryClauseIdentifier(identifier),
                Clause = new EntityTypeQueryClause
                {
                    EntityTypeNames = new EntityTypeNameSet(new[] { "entity" }),
                },
            },
        },
    };

    private static GetResult GetResultFor(params EntityId[] ids) => new()
    {
        Batches = new[]
        {
            new TimestampedEntityBatch { Entities = ids.Select(Snapshot).ToArray() },
        },
    };

    private static QueryResult QueryResultFor(params EntityId[] ids) => new()
    {
        Batches = new[]
        {
            new TimestampedQueryBatch { Entities = ids.Select(QuerySnapshot).ToArray() },
        },
    };

    private static GetChangedEntitiesResult GetChangedResultFor(params EntitySnapshot[] snapshots) => new()
    {
        Entities = snapshots.Select(static snapshot => new ChangedEntitySnapshot { Entity = snapshot }).ToArray(),
    };

    private static IEnumerable<EntityId> Ids(GetResult result)
        => result.Batches.SelectMany(static batch => batch.Entities).Select(static snapshot => snapshot.EntityId);

    private sealed class ControllableExecutor : IEntityQueryExecutor
    {
        private readonly object sync = new();
        private readonly List<Call> calls = new();
        private readonly List<(int Kind, TaskCompletionSource<Call> Tcs)> waiters = new();
        private readonly List<string> queryOrder = new();
        private int concurrency;

        public int MaxConcurrency { get; private set; }

        public int GetCallCount { get; private set; }

        public int CallCount
        {
            get
            {
                lock (this.sync)
                {
                    return this.calls.Count;
                }
            }
        }

        public IReadOnlyList<string> QueryOrder
        {
            get
            {
                lock (this.sync)
                {
                    return this.queryOrder.ToArray();
                }
            }
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken)
            => this.RunAsync<GetResult>(QueryKindGet, request, cancellationToken);

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken)
            => this.RunAsync<QueryResult>(QueryKindQuery, request, cancellationToken);

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken)
            => this.RunAsync<GetChangedEntitiesResult>(QueryKindGetChanged, request, cancellationToken);

        public Task<Call> WaitForCallAsync(int kind, CancellationToken cancellationToken)
        {
            lock (this.sync)
            {
                var existing = this.calls.FirstOrDefault(call => !call.Consumed && call.Kind == kind);
                if (existing is not null)
                {
                    existing.Consumed = true;
                    return Task.FromResult(existing);
                }

                var tcs = new TaskCompletionSource<Call>(TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                var task = tcs.Task.ContinueWith(
                    t =>
                    {
                        registration.Dispose();
                        return t.GetAwaiter().GetResult();
                    },
                    TaskContinuationOptions.ExecuteSynchronously);
                this.waiters.Add((kind, tcs));
                return task;
            }
        }

        public void CompleteAll(object result)
        {
            List<Call> pending;
            lock (this.sync)
            {
                pending = this.calls.Where(call => !call.IsCompleted).ToList();
            }

            foreach (var call in pending)
            {
                call.Complete(result);
            }
        }

        public bool AnyGetForExactly(params EntityId[] ids)
        {
            var target = ids.ToHashSet();
            lock (this.sync)
            {
                return this.calls.Any(call =>
                    call.Kind == QueryKindGet
                    && ((GetRequest)call.Request).Entities
                        .Select(entity => entity.EntityId!.Value)
                        .ToHashSet()
                        .SetEquals(target));
            }
        }

        private Task<TResult> RunAsync<TResult>(int kind, object request, CancellationToken cancellationToken)
            where TResult : class
        {
            Call call;
            lock (this.sync)
            {
                if (kind == QueryKindGet)
                {
                    this.GetCallCount++;
                }

                if (kind == QueryKindQuery)
                {
                    this.queryOrder.Add(((QueryRequest)request).Clauses.First().ClauseIdentifier.Value);
                }

                this.concurrency++;
                this.MaxConcurrency = Math.Max(this.MaxConcurrency, this.concurrency);

                call = new Call(kind, request, cancellationToken);
                this.calls.Add(call);

                var waiterIndex = this.waiters.FindIndex(w => w.Kind == kind);
                if (waiterIndex >= 0)
                {
                    var waiter = this.waiters[waiterIndex];
                    this.waiters.RemoveAt(waiterIndex);
                    call.Consumed = true;
                    waiter.Tcs.TrySetResult(call);
                }
            }

            return AwaitAsync();

            async Task<TResult> AwaitAsync()
            {
                try
                {
                    var result = await call.Gate.Task.ConfigureAwait(false);
                    return (TResult)result;
                }
                finally
                {
                    lock (this.sync)
                    {
                        this.concurrency--;
                    }
                }
            }
        }

        public sealed class Call
        {
            private readonly TaskCompletionSource<bool> cancelledSignal =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Call(int kind, object request, CancellationToken token)
            {
                this.Kind = kind;
                this.Request = request;
                this.Token = token;
                this.Registration = token.Register(() =>
                {
                    this.Gate.TrySetCanceled(token);
                    this.cancelledSignal.TrySetResult(true);
                });
            }

            public int Kind { get; }

            public object Request { get; }

            public CancellationToken Token { get; }

            public TaskCompletionSource<object> Gate { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationTokenRegistration Registration { get; }

            public bool Consumed { get; set; }

            public bool IsCompleted => this.Gate.Task.IsCompleted;

            public void Complete(object result) => this.Gate.TrySetResult(result);

            public void Fault(Exception exception) => this.Gate.TrySetException(exception);

            public Task WaitCancelledAsync(CancellationToken cancellationToken)
                => this.cancelledSignal.Task.WaitAsync(cancellationToken);
        }
    }
}
