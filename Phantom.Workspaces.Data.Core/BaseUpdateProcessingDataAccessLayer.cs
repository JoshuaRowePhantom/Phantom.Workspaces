namespace Phantom.Workspaces.Data;

/// <summary>
/// Passes read operations and update operations through to an underlying data access layer.
/// </summary>
public abstract class BaseUpdateProcessingDataAccessLayer : IDataAccessLayer
{
    protected BaseUpdateProcessingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
    {
        this.UnderlyingDataAccessLayer = underlyingDataAccessLayer;
    }

    protected IDataAccessLayer UnderlyingDataAccessLayer { get; }

    [Obsolete("ExportAsync is very expensive and should only be used for full enumeration in rare cases.")]
    public virtual Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.ExportAsync(request, cancellationToken);
    }

    public virtual Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.GetAsync(request, cancellationToken);
    }

    public virtual Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken);
    }

    public virtual Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.GetHistoryAsync(request, cancellationToken);
    }

    public virtual Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.QueryAsync(request, cancellationToken);
    }

    public virtual Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
    }

    public virtual Task<ProcessQueueResult> ProcessQueueAsync(
        ProcessQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.ProcessQueueAsync(request, cancellationToken);
    }

    public virtual Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(
        ComputeEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.ComputeEmbeddingsAsync(request, cancellationToken);
    }

    public virtual Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(
        UpdateEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UnderlyingDataAccessLayer.UpdateEmbeddingsAsync(request, cancellationToken);
    }
}
