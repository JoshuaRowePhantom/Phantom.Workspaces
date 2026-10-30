using System.Collections.Specialized;
using System.ComponentModel;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services;

/// <summary>Projects persisted session metadata independently of viewer lifetimes.</summary>
internal sealed class RunningAgentSessionDisplaySubscription : IDisposable
{
    private readonly SubscribedQuery query;
    private readonly IRunningAgentChatTable table;
    private readonly HashSet<SubscribedEntityViewModel> observedEntities = [];
    private bool disposed;

    private RunningAgentSessionDisplaySubscription(
        SubscribedQuery query,
        IRunningAgentChatTable table)
    {
        this.query = query;
        this.table = table;
        query.Results.CollectionChanged += this.OnEntitiesChanged;
        table.RunningSessions.CollectionChanged += this.OnSessionsChanged;
        this.RefreshSubscriptions();
    }

    internal static async Task<RunningAgentSessionDisplaySubscription> CreateAsync(
        EntityBroker broker,
        IRunningAgentChatTable table,
        CancellationToken cancellationToken)
    {
        var query = await broker.SubscribeQueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("running-session-display"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["agent-session"]) },
                },
            ],
        }, cancellationToken);
        return new RunningAgentSessionDisplaySubscription(query, table);
    }

    private void OnEntitiesChanged(object? sender, NotifyCollectionChangedEventArgs change)
        => this.RefreshSubscriptions();

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs change)
    {
        foreach (var entity in this.observedEntities)
            this.Publish(entity);
    }

    private void RefreshSubscriptions()
    {
        foreach (var entity in this.observedEntities.Except(this.query.Results).ToArray())
        {
            entity.PropertyChanged -= this.OnEntityChanged;
            this.observedEntities.Remove(entity);
        }
        foreach (var entity in this.query.Results)
        {
            if (this.observedEntities.Add(entity))
                entity.PropertyChanged += this.OnEntityChanged;
            this.Publish(entity);
        }
    }

    private void OnEntityChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(SubscribedEntityViewModel.Data))
            this.Publish((SubscribedEntityViewModel)sender!);
    }

    private void Publish(SubscribedEntityViewModel entity)
    {
        if (entity.Data is { } data
            && data.TryGetProperty("agent-session-id", out var identifier)
            && identifier.GetString() is { Length: > 0 } sessionId)
        {
            this.table.ApplyAuthoritativeDisplayName(
                new AgentSessionId(sessionId),
                RunningAgentChatTable.ReadCanonicalDisplayName(data));
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        this.query.Results.CollectionChanged -= this.OnEntitiesChanged;
        this.table.RunningSessions.CollectionChanged -= this.OnSessionsChanged;
        foreach (var entity in this.observedEntities)
            entity.PropertyChanged -= this.OnEntityChanged;
        this.observedEntities.Clear();
    }
}
