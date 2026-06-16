using System.Text.Json;

namespace Phantom.Workspaces.Data;

public sealed class WorkspaceEntitySessionDataAccessLayer : IDataAccessLayer
{
    private readonly IDataAccessLayer underlyingDataAccessLayer;
    private readonly WorkspaceEntitySession workspaceEntitySession;
    private readonly WorkspaceEntitySessionNameResolver workspaceEntitySessionNameResolver;

    public WorkspaceEntitySessionDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession)
    {
        this.underlyingDataAccessLayer = underlyingDataAccessLayer;
        this.workspaceEntitySession = workspaceEntitySession;
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
        // Bind the current user/computer/profile session meta-variables (e.g. "${USER}") that appear
        // as field-clause comparison values into the concrete session entity ids, so views can query
        // for "the current user" without hard-coding an id. This mirrors the name rewriting in
        // GetAsync.
        var rewrittenClauses = request.Clauses
            .Select(topLevelClause => topLevelClause with { Clause = this.RewriteSessionMetaVariables(topLevelClause.Clause) })
            .ToArray();

        return this.underlyingDataAccessLayer.QueryAsync(request with { Clauses = rewrittenClauses }, cancellationToken);
    }

    private QueryClause RewriteSessionMetaVariables(QueryClause clause)
    {
        switch (clause)
        {
            case AndQueryClause andClause:
                return andClause with { Clauses = andClause.Clauses.Select(this.RewriteSessionMetaVariables).ToArray() };

            case OrQueryClause orClause:
                return orClause with { Clauses = orClause.Clauses.Select(this.RewriteSessionMetaVariables).ToArray() };

            case NotQueryClause notClause:
                return notClause with { Clause = this.RewriteSessionMetaVariables(notClause.Clause) };

            case TopQueryClause topClause:
                return topClause with { Clause = this.RewriteSessionMetaVariables(topClause.Clause) };

            case TransitQueryClause transitClause:
                return transitClause with { MatchClause = this.RewriteSessionMetaVariables(transitClause.MatchClause) };

            case EntityParticipationQueryClause participationClause:
                return participationClause.MustHave is { } mustHave
                    ? participationClause with { MustHave = mustHave with { Clause = this.RewriteSessionMetaVariables(mustHave.Clause) } }
                    : participationClause;

            case EntityFieldQueryClause fieldClause:
                return this.RewriteFieldClauseValue(fieldClause);

            default:
                return clause;
        }
    }

    private EntityFieldQueryClause RewriteFieldClauseValue(EntityFieldQueryClause fieldClause)
    {
        if (fieldClause.Value is not { ValueKind: JsonValueKind.String } value)
        {
            return fieldClause;
        }

        var sessionEntityId = value.GetString() switch
        {
            WorkspaceEntityMetaVariables.User => this.workspaceEntitySession.UserEntityId,
            WorkspaceEntityMetaVariables.Computer => this.workspaceEntitySession.ComputerEntityId,
            WorkspaceEntityMetaVariables.UserProfile => this.workspaceEntitySession.UserComputerProfileEntityId,
            _ => (EntityId?)null,
        };

        return sessionEntityId is { } entityId
            ? fieldClause with { Value = JsonSerializer.SerializeToElement(entityId.Value.ToString()) }
            : fieldClause;
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
