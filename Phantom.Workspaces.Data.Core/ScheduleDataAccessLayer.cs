namespace Phantom.Workspaces.Data;

/// <summary>
/// A decorator that dispatches every <see cref="IDataAccessLayer"/> call onto a <see cref="TaskScheduler"/>,
/// ensuring DAL work never executes on the calling (e.g. UI/dispatcher) thread.
/// </summary>
public sealed class ScheduleDataAccessLayer : IDataAccessLayer
{
    private readonly IDataAccessLayer inner;
    private readonly TaskScheduler scheduler;

    public ScheduleDataAccessLayer(IDataAccessLayer inner, TaskScheduler? scheduler = null)
    {
        this.inner = inner;
        this.scheduler = scheduler ?? TaskScheduler.Default;
    }

    private Task<T> RunAsync<T>(Func<Task<T>> func, CancellationToken cancellationToken)
        => Task.Factory.StartNew(func, cancellationToken, TaskCreationOptions.None, this.scheduler).Unwrap();

    public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.UpdateAsync(request, cancellationToken), cancellationToken);

    public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.GetAsync(request, cancellationToken), cancellationToken);

    public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.QueryAsync(request, cancellationToken), cancellationToken);

    public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.GetHistoryAsync(request, cancellationToken), cancellationToken);

    [Obsolete("ExportAsync is very expensive and should only be used for full enumeration in rare cases.")]
    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.ExportAsync(request, cancellationToken), cancellationToken);

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.GetChangedEntitiesAsync(request, cancellationToken), cancellationToken);

    public Task<ProcessQueueResult> ProcessQueueAsync(ProcessQueueRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.ProcessQueueAsync(request, cancellationToken), cancellationToken);

    public Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(ComputeEmbeddingsRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.ComputeEmbeddingsAsync(request, cancellationToken), cancellationToken);

    public Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(UpdateEmbeddingsRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => this.inner.UpdateEmbeddingsAsync(request, cancellationToken), cancellationToken);
}
