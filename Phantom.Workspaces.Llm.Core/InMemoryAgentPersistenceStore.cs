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

    internal void Reset() => this.sessions.Clear();

    public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Agent.AgentSessionId);

        var newMessages = request.NewMessages ?? [];
        SessionData session = this.sessions.GetOrAdd(request.Agent.AgentSessionId, static _ => new SessionData());
        lock (session)
        {
            session.Agent = request.Agent with
            {
                AgentSessionJson = request.Agent.AgentSessionJson ?? session.Agent?.AgentSessionJson,
                AgentDefinitionJson = request.Agent.AgentDefinitionJson ?? session.Agent?.AgentDefinitionJson,
                CopilotSdkSessionId = request.Agent.CopilotSdkSessionId ?? session.Agent?.CopilotSdkSessionId,
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
}
