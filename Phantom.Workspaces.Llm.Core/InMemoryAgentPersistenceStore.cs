using System.Collections.Concurrent;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

internal sealed class InMemoryAgentPersistenceStore : IAgentPersistenceStore
{
    private sealed class SessionData
    {
        public PersistedAgent? Agent { get; set; }

        public List<Microsoft.Extensions.AI.ChatMessage> Messages { get; } = [];
    }

    private readonly ConcurrentDictionary<string, SessionData> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ParentSessionId, string ChildSessionId), byte> subAgentLinks = new();
    private readonly TimeProvider timeProvider;

    public InMemoryAgentPersistenceStore()
        : this(TimeProvider.System)
    {
    }

    public InMemoryAgentPersistenceStore(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal void Reset()
    {
        this.sessions.Clear();
        this.subAgentLinks.Clear();
    }

    public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Agent.AgentSessionId);

        var newMessages = request.NewMessages ?? [];
        SessionData session = this.sessions.GetOrAdd(request.Agent.AgentSessionId, static _ => new SessionData());
        lock (session)
        {
            // Stamp LastUpdatedUtc on every write so restore can surface the original
            // last-activity time (issue #1140). Callers should not have to supply it; if they
            // do, honour it so tests can inject deterministic timestamps.
            var stampedLastUpdatedUtc = request.Agent.LastUpdatedUtc
                ?? this.timeProvider.GetUtcNow().UtcDateTime;

            session.Agent = request.Agent with
            {
                AgentSessionJson = request.Agent.AgentSessionJson ?? session.Agent?.AgentSessionJson,
                AgentDefinitionJson = request.Agent.AgentDefinitionJson ?? session.Agent?.AgentDefinitionJson,
                CopilotSdkSessionId = request.Agent.CopilotSdkSessionId ?? session.Agent?.CopilotSdkSessionId,
                LastUpdatedUtc = stampedLastUpdatedUtc,
            };
            if (newMessages.Length > 0)
            {
                session.Messages.AddRange(newMessages);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PersistedAgent?> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentSessionId);

        if (!this.sessions.TryGetValue(request.AgentSessionId, out SessionData? session))
        {
            return ValueTask.FromResult<PersistedAgent?>(null);
        }

        lock (session)
        {
            return ValueTask.FromResult(session.Agent);
        }
    }

    public ValueTask<Microsoft.Extensions.AI.ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentSessionId);

        if (!this.sessions.TryGetValue(request.AgentSessionId, out SessionData? session))
        {
            return ValueTask.FromResult(Array.Empty<Microsoft.Extensions.AI.ChatMessage>());
        }

        lock (session)
        {
            return ValueTask.FromResult(session.Messages.ToArray());
        }
    }

    public ValueTask AddSubAgentLinkAsync(
        string parentSessionId,
        string childSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);

        this.subAgentLinks[(parentSessionId, childSessionId)] = 0;

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);

        var result = this.subAgentLinks.Keys
            .Where(k => k.ParentSessionId == parentSessionId)
            .Select(k => new AgentSessionId(k.ChildSessionId))
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<AgentSessionId>>(result);
    }
}
