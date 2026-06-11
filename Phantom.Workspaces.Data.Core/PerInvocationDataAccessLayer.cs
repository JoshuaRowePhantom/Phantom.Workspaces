namespace Phantom.Workspaces.Data;

/// <summary>
/// Creates a new underlying data access layer for each invocation.
/// </summary>
public sealed class PerInvocationDataAccessLayer : IDataAccessLayer
{
    private readonly Func<IDataAccessLayer> createDataAccessLayer;

    public PerInvocationDataAccessLayer(
        Func<IDataAccessLayer> createDataAccessLayer)
    {
        this.createDataAccessLayer = createDataAccessLayer;
    }

    [Obsolete("ExportAsync is very expensive and should only be used for full enumeration in rare cases.")]
    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ExecuteAsync(
            dataAccessLayer => dataAccessLayer.ExportAsync(request, cancellationToken));
    }

    public Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ExecuteAsync(
            dataAccessLayer => dataAccessLayer.GetAsync(request, cancellationToken));
    }

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ExecuteAsync(
            dataAccessLayer => dataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken));
    }

    public Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ExecuteAsync(
            dataAccessLayer => dataAccessLayer.GetHistoryAsync(request, cancellationToken));
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ExecuteAsync(
            dataAccessLayer => dataAccessLayer.QueryAsync(request, cancellationToken));
    }

    public Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ExecuteAsync(
            dataAccessLayer => dataAccessLayer.UpdateAsync(request, cancellationToken));
    }

    private async Task<T> ExecuteAsync<T>(
        Func<IDataAccessLayer, Task<T>> execute)
    {
        var dataAccessLayer = this.createDataAccessLayer();
        try
        {
            return await execute(dataAccessLayer).ConfigureAwait(false);
        }
        finally
        {
            if (dataAccessLayer is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (dataAccessLayer is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
