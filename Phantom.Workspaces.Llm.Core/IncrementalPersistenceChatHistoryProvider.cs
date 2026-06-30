using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

internal sealed class IncrementalPersistenceChatHistoryProvider : ChatHistoryProvider
{
    private sealed class SessionState
    {
        public required string AgentSessionId { get; set; }
    }

    private readonly ProviderSessionState<SessionState> sessionState;
    private readonly BsonDocument? agentDefinitionJson;
    private readonly IAgentPersistenceStore store;
    private volatile string? copilotSdkSessionId;
    private volatile BsonDocument? cachedAgentSessionJson;
    private Func<AgentSession, CancellationToken, ValueTask<BsonDocument>>? serializeSession;

    public IncrementalPersistenceChatHistoryProvider(
        AgentDefinition? agentDefinition,
        IAgentPersistenceStore store)
        : base(null, null, null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.agentDefinitionJson = agentDefinition is null
            ? null
            : BsonDocument.Parse(agentDefinition.ToJson());
        this.sessionState = new ProviderSessionState<SessionState>(
            stateInitializer: InitializeSessionState,
            stateKey: nameof(IncrementalPersistenceChatHistoryProvider));
    }

    public void SetSessionSerializer(
        Func<AgentSession, CancellationToken, ValueTask<BsonDocument>> serializeSession)
    {
        this.serializeSession = serializeSession ?? throw new ArgumentNullException(nameof(serializeSession));
    }

    /// <summary>
    /// Records the live GitHub Copilot SDK session id so it is persisted alongside the agent state
    /// and can be used to resume the CLI session (with its history) after a restart (issue #3).
    /// A known id is never cleared by a subsequent null.
    /// </summary>
    public void SetCopilotSdkSessionId(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            this.copilotSdkSessionId = sessionId;
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(IAgentPersistenceStore))
        {
            return this.store;
        }

        return base.GetService(serviceType, serviceKey);
    }

    public string ExtractAgentSessionId(AgentSession? session)
    {
        if (session is null)
        {
            return Guid.NewGuid().ToString("n");
        }

        SessionState state = this.sessionState.GetOrInitializeState(session)
            ?? throw new InvalidOperationException("Unable to initialize agent persistence session state.");

        return state.AgentSessionId;
    }

    public void SetAgentSessionId(AgentSession? session, string agentSessionId)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId))
        {
            throw new ArgumentException("Agent session id is required.", nameof(agentSessionId));
        }

        if (session is null)
        {
            return;
        }

        SessionState state = this.sessionState.GetOrInitializeState(session)
            ?? throw new InvalidOperationException("Unable to initialize agent persistence session state.");
        state.AgentSessionId = agentSessionId;
    }

    /// <summary>
    /// Builds a <see cref="PersistedAgent"/> snapshot for the given session using the session JSON
    /// that was cached during the most recent <see cref="ProvideChatHistoryAsync"/> call.
    /// </summary>
    internal PersistedAgent BuildPersistedAgent(AgentSession session)
    {
        return new PersistedAgent
        {
            AgentSessionId = this.ExtractAgentSessionId(session),
            AgentSessionJson = this.cachedAgentSessionJson,
            AgentDefinitionJson = this.agentDefinitionJson,
            CopilotSdkSessionId = this.copilotSdkSessionId,
        };
    }

    private static SessionState InitializeSessionState(AgentSession? session)
    {
        return new SessionState { AgentSessionId = Guid.NewGuid().ToString("n") };
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Session);

        var agentSessionId = this.ExtractAgentSessionId(context.Session);
        var existingMessages = await this.store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = agentSessionId },
            cancellationToken).ConfigureAwait(false);

        // Always serialize and cache so the streaming middleware can build PersistedAgent mid-stream.
        var sessionJson = await this.SerializeSessionAsync(context.Session, cancellationToken).ConfigureAwait(false);
        this.cachedAgentSessionJson = sessionJson;

        var requestMessages = context.RequestMessages.ToArray();
        if (requestMessages.Length > 0)
        {
            await this.store.StoreAsync(
                new StoreRequestAgent
                {
                    Agent = new PersistedAgent
                    {
                        AgentSessionId = agentSessionId,
                        AgentSessionJson = sessionJson,
                        AgentDefinitionJson = this.agentDefinitionJson,
                        CopilotSdkSessionId = this.copilotSdkSessionId,
                    },
                    NewMessages = requestMessages,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return existingMessages;
    }

    /// <summary>
    /// No-op: the <see cref="StreamingPersistenceMiddleware"/> owns all response-message
    /// persistence. Making this a no-op avoids double-writes without any deduplication bookkeeping.
    /// </summary>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private async ValueTask<BsonDocument> SerializeSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var serializeSession = this.serializeSession
            ?? throw new InvalidOperationException("Session serializer was not configured before persistence was used.");

        return await serializeSession(session, cancellationToken).ConfigureAwait(false);
    }
}
