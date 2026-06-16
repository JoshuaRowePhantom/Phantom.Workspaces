namespace Phantom.Workspaces.Data;

public sealed class WorkspaceEntitySessionDataAccessLayer : IDataAccessLayer
{
    private readonly IDataAccessLayer underlyingDataAccessLayer;
    private readonly WorkspaceEntitySessionNameResolver workspaceEntitySessionNameResolver;

    public WorkspaceEntitySessionDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession)
    {
        this.underlyingDataAccessLayer = underlyingDataAccessLayer;
        this.workspaceEntitySessionNameResolver = new WorkspaceEntitySessionNameResolver(underlyingDataAccessLayer, workspaceEntitySession);
    }

    public Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
    }

    public Task<ProcessQueueResult> ProcessQueueAsync(
        ProcessQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.ProcessQueueAsync(request, cancellationToken);
    }

    public Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(
        ComputeEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.ComputeEmbeddingsAsync(request, cancellationToken);
    }

    public Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(
        UpdateEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.UpdateEmbeddingsAsync(request, cancellationToken);
    }

    public async Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Entities.Any(entityRequest => this.workspaceEntitySessionNameResolver.HasMetaVariables(entityRequest.EntityName)))
        {
            return await this.underlyingDataAccessLayer.GetAsync(request, cancellationToken);
        }

        var resolvedNames = await this.workspaceEntitySessionNameResolver.GetResolvedNamesAsync(cancellationToken);
        var rewrittenEntities = new List<GetEntityRequest>();
        foreach (var entityRequest in request.Entities)
        {
            if (!this.workspaceEntitySessionNameResolver.HasMetaVariables(entityRequest.EntityName)
                || entityRequest.EntityName is not EntityName entityName)
            {
                rewrittenEntities.Add(entityRequest);
                continue;
            }

            foreach (var rewrittenEntityName in this.workspaceEntitySessionNameResolver.RewriteMetaVariables(entityName, resolvedNames))
            {
                rewrittenEntities.Add(
                    entityRequest with
                    {
                        EntityName = rewrittenEntityName,
                    });
            }
        }

        var rewrittenRequest = request with
        {
            Entities = rewrittenEntities,
        };

        return await this.underlyingDataAccessLayer.GetAsync(rewrittenRequest, cancellationToken);
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.QueryAsync(request, cancellationToken);
    }

    public Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.GetHistoryAsync(request, cancellationToken);
    }

    [Obsolete("ExportAsync is very expensive and should only be used for full enumeration in rare cases.")]
    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.ExportAsync(request, cancellationToken);
    }

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken);
    }
}
