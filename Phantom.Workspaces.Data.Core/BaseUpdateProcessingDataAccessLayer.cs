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
}
