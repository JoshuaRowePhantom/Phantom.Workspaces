using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Executes the three background read operations <see cref="EntityBrokerQuerySatisfier"/> coordinates.
/// Abstracted so the satisfier can be unit-tested against a gated fake without a real repository.
/// </summary>
internal interface IEntityQueryExecutor
{
    Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken);

    Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken);

    Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IEntityQueryExecutor"/> that offloads each data-access call onto the thread pool
/// via <see cref="Task.Run(Func{Task})"/>, matching the pre-existing <see cref="EntityBroker"/> behaviour.
/// </summary>
internal sealed class DataAccessLayerQueryExecutor : IEntityQueryExecutor
{
    private readonly Func<IDataAccessLayer> dataAccessLayerProvider;

    public DataAccessLayerQueryExecutor(Func<IDataAccessLayer> dataAccessLayerProvider)
    {
        this.dataAccessLayerProvider = dataAccessLayerProvider ?? throw new ArgumentNullException(nameof(dataAccessLayerProvider));
    }

    public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken)
        => Task.Run(() => this.dataAccessLayerProvider().GetAsync(request, cancellationToken), cancellationToken);

    public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken)
        => Task.Run(() => this.dataAccessLayerProvider().QueryAsync(request, cancellationToken), cancellationToken);

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken)
        => Task.Run(() => this.dataAccessLayerProvider().GetChangedEntitiesAsync(request, cancellationToken), cancellationToken);
}

/// <summary>
/// Coordinates <see cref="EntityBroker"/>'s background reads so one executed query can satisfy others it
/// covers, minimizing redundant data-access round-trips (#1328).
/// <para>
/// The satisfier applies, per issued request: <b>dedup</b> (identical in-flight requests share one wire
/// call), <b>issuance-time subsumption</b> (a broad get / full-scope get-changed serves a narrow get),
/// <b>result-time subsumption</b> (a broad query whose results happen to cover a narrow get serves it),
/// a <b>periodic single-slot throttle</b> (tick reads run one-at-a-time), a <b>parallel on-demand path</b>
/// (user-driven reads are never throttled), <b>cross-trigger</b> (an on-demand read that covers a pending
/// periodic read refreshes it immediately), and <b>skip-refresh</b> (a subsumed periodic read does not
/// re-issue while a subsuming read is live).
/// </para>
/// </summary>
internal sealed class EntityBrokerQuerySatisfier : IAsyncDisposable
{
    private enum QueryKind
    {
        Get,
        Query,
        GetChanged,
    }

    private enum ExecutionClass
    {
        OnDemand,
        Periodic,
    }

    private sealed class Awaiter
    {
        public required TaskCompletionSource<object> Tcs { get; init; }

        public required CancellationToken Token { get; init; }

        public CancellationTokenRegistration Registration { get; set; }

        public bool Cancelled { get; set; }
    }

    private sealed class PendingQuery
    {
        public required string Key { get; init; }

        public required QueryKind Kind { get; init; }

        public required ExecutionClass Class { get; init; }

        public required object Request { get; init; }

        /// <summary>True when this is a "simple" id-based <see cref="GetRequest"/> eligible for subsumption.</summary>
        public bool IsSimpleIdGet { get; init; }

        /// <summary>The id-set targeted by a simple id-based get (null otherwise).</summary>
        public HashSet<EntityId>? TargetIds { get; init; }

        public List<Awaiter> Awaiters { get; } = new();

        public int LiveAwaiters { get; set; }

        // Subsuming lists, maintained separately per the owner's requirement.
        public List<PendingQuery> IssuanceTimeSubsumers { get; } = new();

        public List<PendingQuery> ResultTimeSubsumers { get; } = new();

        // In-flight queries this get is deferring on to see whether their results cover it.
        public List<PendingQuery> PendingResultTimeSubsumers { get; } = new();

        // Pending queries served by THIS query's result.
        public List<PendingQuery> SliceTargets { get; } = new();

        public bool Started { get; set; }

        public bool Completed { get; set; }

        public CancellationTokenSource? ExecutionCts { get; set; }

        public bool HasLiveSubsumer =>
            this.IssuanceTimeSubsumers.Any(static s => !s.Completed)
            || this.ResultTimeSubsumers.Any(static s => !s.Completed)
            || this.PendingResultTimeSubsumers.Any(static s => !s.Completed);
    }

    private readonly IEntityQueryExecutor executor;
    private readonly Func<EntityId, EntitySnapshot?> cacheResolver;
    private readonly object gate = new();
    private readonly Dictionary<string, PendingQuery> pendingByKey = new(StringComparer.Ordinal);
    private readonly List<PendingQuery> allPending = new();
    private readonly Channel<PendingQuery> periodicQueue =
        Channel.CreateUnbounded<PendingQuery>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly Task periodicLoop;
    private bool disposed;

    public EntityBrokerQuerySatisfier(
        IEntityQueryExecutor executor,
        Func<EntityId, EntitySnapshot?>? cacheResolver = null)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.cacheResolver = cacheResolver ?? (static _ => null);
        this.periodicLoop = Task.Run(this.RunPeriodicLoopAsync);
    }

    public Task<GetResult> SatisfyOrIssueOnDemandGetAsync(GetRequest request, CancellationToken cancellationToken)
        => this.IssueAsync<GetResult>(QueryKind.Get, request, ExecutionClass.OnDemand, cancellationToken);

    public Task<QueryResult> SatisfyOrIssueOnDemandQueryAsync(QueryRequest request, CancellationToken cancellationToken)
        => this.IssueAsync<QueryResult>(QueryKind.Query, request, ExecutionClass.OnDemand, cancellationToken);

    public Task<GetChangedEntitiesResult> SatisfyOrIssueOnDemandGetChangedAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken)
        => this.IssueAsync<GetChangedEntitiesResult>(QueryKind.GetChanged, request, ExecutionClass.OnDemand, cancellationToken);

    public Task<GetResult> SatisfyOrEnqueuePeriodicGetAsync(GetRequest request, CancellationToken cancellationToken)
        => this.IssueAsync<GetResult>(QueryKind.Get, request, ExecutionClass.Periodic, cancellationToken);

    public Task<QueryResult> SatisfyOrEnqueuePeriodicQueryAsync(QueryRequest request, CancellationToken cancellationToken)
        => this.IssueAsync<QueryResult>(QueryKind.Query, request, ExecutionClass.Periodic, cancellationToken);

    public Task<GetChangedEntitiesResult> SatisfyOrEnqueuePeriodicGetChangedAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken)
        => this.IssueAsync<GetChangedEntitiesResult>(QueryKind.GetChanged, request, ExecutionClass.Periodic, cancellationToken);

    private async Task<TResult> IssueAsync<TResult>(
        QueryKind kind,
        object request,
        ExecutionClass executionClass,
        CancellationToken cancellationToken)
        where TResult : class
    {
        Task<object> task;
        PendingQuery? toExecute = null;
        bool canceled = false;

        lock (this.gate)
        {
            if (this.disposed || cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                task = null!;
            }
            else
            {
                task = this.IssueUnderGate(kind, request, executionClass, cancellationToken, out toExecute);
            }
        }

        if (canceled)
        {
            return await CanceledAsync<TResult>(cancellationToken).ConfigureAwait(false);
        }

        if (toExecute is PendingQuery pendingToExecute)
        {
            _ = Task.Run(() => this.ExecuteAsync(pendingToExecute));
        }

        var result = await task.ConfigureAwait(false);
        return (TResult)result;
    }

    private Task<object> IssueUnderGate(
        QueryKind kind,
        object request,
        ExecutionClass executionClass,
        CancellationToken cancellationToken,
        out PendingQuery? toExecute)
    {
        toExecute = null;
        Task<object> task;

        {
            var key = BuildKey(kind, request);
            if (this.pendingByKey.TryGetValue(key, out var existing) && !existing.Completed)
            {
                // Dedup: identical in-flight request shares the same underlying call.
                task = this.AttachAwaiter(existing, cancellationToken);
            }
            else
            {
                var (isSimpleIdGet, targetIds) = AnalyzeGet(kind, request);
                var pending = new PendingQuery
                {
                    Key = key,
                    Kind = kind,
                    Class = executionClass,
                    Request = request,
                    IsSimpleIdGet = isSimpleIdGet,
                    TargetIds = targetIds,
                };

                this.pendingByKey[key] = pending;
                this.allPending.Add(pending);
                task = this.AttachAwaiter(pending, cancellationToken);

                this.ComputeSubsumersForNewQuery(pending);
                this.ApplyAdmissionScan(pending);

                if (!pending.HasLiveSubsumer)
                {
                    if (executionClass == ExecutionClass.OnDemand)
                    {
                        toExecute = pending;
                    }
                    else
                    {
                        this.periodicQueue.Writer.TryWrite(pending);
                    }
                }
            }
        }

        return task;
    }

    private Task<object> AttachAwaiter(PendingQuery pending, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = new Awaiter { Tcs = tcs, Token = cancellationToken };
        pending.Awaiters.Add(awaiter);
        pending.LiveAwaiters++;

        if (cancellationToken.CanBeCanceled)
        {
            awaiter.Registration = cancellationToken.Register(() => this.OnAwaiterCancelled(pending, awaiter));
        }

        return tcs.Task;
    }

    private void OnAwaiterCancelled(PendingQuery pending, Awaiter awaiter)
    {
        CancellationTokenSource? toCancel = null;
        lock (this.gate)
        {
            if (awaiter.Cancelled || pending.Completed)
            {
                return;
            }

            awaiter.Cancelled = true;
            pending.LiveAwaiters--;

            // Only cancel the underlying call once the last attached awaiter has gone.
            if (pending.LiveAwaiters == 0)
            {
                toCancel = pending.ExecutionCts;
            }
        }

        awaiter.Tcs.TrySetCanceled(awaiter.Token);
        toCancel?.Cancel();
    }

    /// <summary>Populates <paramref name="b"/>'s subsuming lists by scanning existing in-flight queries.</summary>
    private void ComputeSubsumersForNewQuery(PendingQuery b)
    {
        if (!b.IsSimpleIdGet || b.TargetIds is null)
        {
            return;
        }

        foreach (var a in this.allPending)
        {
            if (ReferenceEquals(a, b) || a.Completed)
            {
                continue;
            }

            if (IssuanceSubsumes(a, b))
            {
                b.IssuanceTimeSubsumers.Add(a);
                a.SliceTargets.Add(b);
            }
        }

        if (b.IssuanceTimeSubsumers.Count > 0)
        {
            return;
        }

        // No provable static subsumer: defer on any in-flight query whose results might cover this get.
        foreach (var a in this.allPending)
        {
            if (ReferenceEquals(a, b) || a.Completed || a.Kind != QueryKind.Query)
            {
                continue;
            }

            b.PendingResultTimeSubsumers.Add(a);
            a.SliceTargets.Add(b);
        }
    }

    /// <summary>
    /// When a broad query <paramref name="a"/> is admitted, attach existing pending narrow gets that it
    /// subsumes (cross-trigger and skip-refresh both flow from this).
    /// </summary>
    private void ApplyAdmissionScan(PendingQuery a)
    {
        foreach (var b in this.allPending)
        {
            if (ReferenceEquals(a, b) || b.Completed || b.Started || !b.IsSimpleIdGet || b.TargetIds is null)
            {
                continue;
            }

            if (b.IssuanceTimeSubsumers.Contains(a) || b.PendingResultTimeSubsumers.Contains(a))
            {
                continue;
            }

            if (IssuanceSubsumes(a, b))
            {
                b.IssuanceTimeSubsumers.Add(a);
                a.SliceTargets.Add(b);
            }
            else if (a.Kind == QueryKind.Query && b.IssuanceTimeSubsumers.Count == 0)
            {
                b.PendingResultTimeSubsumers.Add(a);
                a.SliceTargets.Add(b);
            }
        }
    }

    private async Task ExecuteAsync(PendingQuery pending)
    {
        CancellationToken token;
        lock (this.gate)
        {
            if (pending.Completed || pending.Started)
            {
                return;
            }

            if (pending.LiveAwaiters == 0)
            {
                // Every caller cancelled before we started; abandon without a wire call.
                this.CompleteRegistryRemoval(pending);
                pending.Completed = true;
                return;
            }

            pending.Started = true;

            var liveAwaiters = pending.Awaiters.Where(static a => !a.Cancelled).ToList();
            if (liveAwaiters.Count == 1)
            {
                // Single caller: forward its token verbatim so the data-access layer observes the exact
                // token the caller supplied (cancelling it cancels only this in-flight call).
                pending.ExecutionCts = null;
                token = liveAwaiters[0].Token;
            }
            else
            {
                // Multiple callers (dedup / subsumption): the underlying call is cancelled only once the
                // last attached awaiter has cancelled (see OnAwaiterCancelled).
                pending.ExecutionCts = new CancellationTokenSource();
                token = pending.ExecutionCts.Token;
            }
        }

        object? result = null;
        Exception? fault = null;
        try
        {
            result = pending.Kind switch
            {
                QueryKind.Get => await this.executor.GetAsync((GetRequest)pending.Request, token).ConfigureAwait(false),
                QueryKind.Query => await this.executor.QueryAsync((QueryRequest)pending.Request, token).ConfigureAwait(false),
                QueryKind.GetChanged => await this.executor.GetChangedEntitiesAsync((GetChangedEntitiesRequest)pending.Request, token).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown query kind {pending.Kind}."),
            };
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        this.CompletePending(pending, result, fault);
    }

    private void CompletePending(PendingQuery pending, object? result, Exception? fault)
    {
        List<Awaiter> awaiters;
        List<PendingQuery> sliceTargets;
        var fallthroughs = new List<PendingQuery>();

        lock (this.gate)
        {
            if (pending.Completed)
            {
                return;
            }

            pending.Completed = true;
            this.CompleteRegistryRemoval(pending);
            awaiters = pending.Awaiters.ToList();
            sliceTargets = pending.SliceTargets.ToList();
        }

        foreach (var awaiter in awaiters)
        {
            CompleteAwaiter(awaiter, result, fault);
        }

        foreach (var target in sliceTargets)
        {
            this.ResolveSliceTarget(pending, target, result, fault, fallthroughs);
        }

        foreach (var target in fallthroughs)
        {
            _ = Task.Run(() => this.ExecuteAsync(target));
        }
    }

    private void ResolveSliceTarget(
        PendingQuery source,
        PendingQuery target,
        object? sourceResult,
        Exception? sourceFault,
        List<PendingQuery> fallthroughs)
    {
        bool covered = false;
        object? projected = null;
        bool scheduleFallthrough = false;
        List<Awaiter>? targetAwaiters = null;

        lock (this.gate)
        {
            if (target.Completed)
            {
                return;
            }

            target.IssuanceTimeSubsumers.Remove(source);
            target.PendingResultTimeSubsumers.Remove(source);
            target.ResultTimeSubsumers.Remove(source);

            if (sourceFault is null && sourceResult is not null && target.TargetIds is not null)
            {
                (covered, projected) = this.TryProject(source, sourceResult, target.TargetIds);
                if (covered)
                {
                    target.ResultTimeSubsumers.Add(source);
                    target.Completed = true;
                    this.CompleteRegistryRemoval(target);
                    targetAwaiters = target.Awaiters.ToList();
                }
            }

            if (!covered)
            {
                // Source did not cover the target (fault, or results lacked the ids).
                if (!target.HasLiveSubsumer && !target.Started)
                {
                    if (target.Class == ExecutionClass.OnDemand)
                    {
                        scheduleFallthrough = true;
                    }
                    else
                    {
                        this.periodicQueue.Writer.TryWrite(target);
                    }
                }
            }
        }

        if (covered && targetAwaiters is not null)
        {
            foreach (var awaiter in targetAwaiters)
            {
                CompleteAwaiter(awaiter, projected, null);
            }

            // Recurse: the target may itself have slice targets waiting on it.
            List<PendingQuery> nestedTargets;
            lock (this.gate)
            {
                nestedTargets = target.SliceTargets.ToList();
            }

            foreach (var nested in nestedTargets)
            {
                this.ResolveSliceTarget(target, nested, projected, null, fallthroughs);
            }
        }

        if (scheduleFallthrough)
        {
            fallthroughs.Add(target);
        }
    }

    private static void CompleteAwaiter(Awaiter awaiter, object? result, Exception? fault)
    {
        awaiter.Registration.Dispose();
        if (awaiter.Cancelled)
        {
            return;
        }

        if (fault is not null)
        {
            awaiter.Tcs.TrySetException(fault);
        }
        else
        {
            awaiter.Tcs.TrySetResult(result!);
        }
    }

    private void CompleteRegistryRemoval(PendingQuery pending)
    {
        if (this.pendingByKey.TryGetValue(pending.Key, out var mapped) && ReferenceEquals(mapped, pending))
        {
            this.pendingByKey.Remove(pending.Key);
        }

        this.allPending.Remove(pending);
    }

    private async Task RunPeriodicLoopAsync()
    {
        var reader = this.periodicQueue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var pending))
            {
                bool execute;
                lock (this.gate)
                {
                    execute = !pending.Completed && !pending.Started && pending.LiveAwaiters > 0 && !pending.HasLiveSubsumer;
                    if (!execute && !pending.Completed && !pending.Started && pending.LiveAwaiters == 0 && !pending.HasLiveSubsumer)
                    {
                        // Abandoned (all callers cancelled) and not served by a subsumer.
                        pending.Completed = true;
                        this.CompleteRegistryRemoval(pending);
                    }
                }

                if (execute)
                {
                    // Awaited so periodic reads run strictly one-at-a-time.
                    await this.ExecuteAsync(pending).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Returns whether <paramref name="a"/>'s coverage is a static superset of <paramref name="b"/>'s.</summary>
    private static bool IssuanceSubsumes(PendingQuery a, PendingQuery b)
    {
        if (b.TargetIds is null || b.TargetIds.Count == 0)
        {
            return false;
        }

        switch (a.Kind)
        {
            case QueryKind.Get:
                return a.IsSimpleIdGet && a.TargetIds is not null && b.TargetIds.IsSubsetOf(a.TargetIds);

            case QueryKind.GetChanged:
                var changed = (GetChangedEntitiesRequest)a.Request;
                var covered = changed.EntityIdTimestamps.Select(static t => t.EntityId).ToHashSet();
                return b.TargetIds.IsSubsetOf(covered);

            default:
                return false;
        }
    }

    private (bool Covered, object? Projected) TryProject(PendingQuery source, object sourceResult, HashSet<EntityId> targetIds)
    {
        switch (source.Kind)
        {
            case QueryKind.Get:
            {
                var getResult = (GetResult)sourceResult;
                var snapshots = getResult.Batches
                    .SelectMany(static batch => batch.Entities)
                    .Where(snapshot => targetIds.Contains(snapshot.EntityId))
                    .ToArray();
                return (true, BuildGetResult(snapshots));
            }

            case QueryKind.Query:
            {
                var queryResult = (QueryResult)sourceResult;
                var byId = queryResult.Batches
                    .SelectMany(static batch => batch.Entities)
                    .Where(snapshot => targetIds.Contains(snapshot.EntityId))
                    .Cast<EntitySnapshot>()
                    .ToArray();
                var presentIds = byId.Select(static snapshot => snapshot.EntityId).ToHashSet();
                if (!targetIds.IsSubsetOf(presentIds))
                {
                    // Results did not actually cover every target id.
                    return (false, null);
                }

                return (true, BuildGetResult(byId));
            }

            case QueryKind.GetChanged:
            {
                var changedResult = (GetChangedEntitiesResult)sourceResult;
                var changedById = changedResult.Entities
                    .Where(static entity => entity.Entity is not null)
                    .ToDictionary(static entity => entity.Entity!.EntityId, static entity => entity.Entity!);

                var resolved = new List<EntitySnapshot>();
                foreach (var id in targetIds)
                {
                    if (changedById.TryGetValue(id, out var snapshot))
                    {
                        resolved.Add(snapshot);
                        continue;
                    }

                    // Not-changed (or absent): the target's cached snapshot is still fresh.
                    var cached = this.cacheResolver(id);
                    if (cached is null)
                    {
                        // Cache miss: cannot serve from this result; fall through to a fresh narrow call.
                        return (false, null);
                    }

                    resolved.Add(cached);
                }

                return (true, BuildGetResult(resolved));
            }

            default:
                return (false, null);
        }
    }

    private static GetResult BuildGetResult(IReadOnlyCollection<EntitySnapshot> snapshots)
        => new()
        {
            Batches = new[]
            {
                new TimestampedEntityBatch
                {
                    Entities = snapshots.ToArray(),
                },
            },
        };

    private static (bool IsSimpleIdGet, HashSet<EntityId>? TargetIds) AnalyzeGet(QueryKind kind, object request)
    {
        if (kind != QueryKind.Get)
        {
            return (false, null);
        }

        var get = (GetRequest)request;
        if (get.Entities.Count == 0
            || get.Properties is not null
            || get.RelationshipsToReturn is not null)
        {
            return (false, null);
        }

        var ids = new HashSet<EntityId>();
        foreach (var entity in get.Entities)
        {
            if (entity.EntityId is not EntityId id
                || entity.EnumerateChildren != EnumerateChildrenAction.EnumerateSelf
                || entity.Properties is not null
                || entity.RelationshipsToReturn is not null)
            {
                return (false, null);
            }

            ids.Add(id);
        }

        return (true, ids);
    }

    private static string BuildKey(QueryKind kind, object request)
        => kind switch
        {
            QueryKind.Get => "G:" + JsonSerializer.Serialize((GetRequest)request),
            QueryKind.Query => "Q:" + JsonSerializer.Serialize((QueryRequest)request),
            QueryKind.GetChanged => "C:" + JsonSerializer.Serialize((GetChangedEntitiesRequest)request),
            _ => throw new InvalidOperationException($"Unknown query kind {kind}."),
        };

    private static Task<TResult> CanceledAsync<TResult>(CancellationToken cancellationToken)
        => Task.FromCanceled<TResult>(
            cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));

    public async ValueTask DisposeAsync()
    {
        List<PendingQuery> pendingSnapshot;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            pendingSnapshot = this.allPending.ToList();
        }

        this.periodicQueue.Writer.TryComplete();
        this.shutdownCts.Cancel();

        try
        {
            await this.periodicLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var pending in pendingSnapshot)
        {
            List<Awaiter> awaiters;
            lock (this.gate)
            {
                if (pending.Completed)
                {
                    continue;
                }

                pending.Completed = true;
                awaiters = pending.Awaiters.ToList();
            }

            foreach (var awaiter in awaiters)
            {
                awaiter.Registration.Dispose();
                awaiter.Tcs.TrySetCanceled();
            }
        }

        this.shutdownCts.Dispose();
    }
}
