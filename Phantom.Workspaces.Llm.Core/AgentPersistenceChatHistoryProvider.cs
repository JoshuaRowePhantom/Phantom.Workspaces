using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentPersistenceChatHistoryProvider : ChatHistoryProvider
{
    private sealed class SessionState
    {
        public required string AgentSessionId { get; set; }
    }

    private readonly ProviderSessionState<SessionState> sessionState;
    private readonly BsonDocument? agentDefinitionJson;
    private readonly IAgentPersistenceStore store;
    private Func<AgentSession, CancellationToken, ValueTask<BsonDocument>>? serializeSession;

    public AgentPersistenceChatHistoryProvider(
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
            stateKey: nameof(AgentPersistenceChatHistoryProvider));
    }

    public void SetSessionSerializer(
        Func<AgentSession, CancellationToken, ValueTask<BsonDocument>> serializeSession)
    {
        this.serializeSession = serializeSession ?? throw new ArgumentNullException(nameof(serializeSession));
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

        var requestMessages = context.RequestMessages.ToArray();
        if (requestMessages.Length > 0)
        {
            await this.store.StoreAsync(
                new StoreRequestAgent
                {
                    Agent = new PersistedAgent
                    {
                        AgentSessionId = agentSessionId,
                        AgentSessionJson = await this.SerializeSessionAsync(context.Session, cancellationToken).ConfigureAwait(false),
                        AgentDefinitionJson = this.agentDefinitionJson,
                    },
                    NewMessages = requestMessages,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return await this.store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = agentSessionId },
            cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Session);

        var agentSessionId = this.ExtractAgentSessionId(context.Session);

        var responseMessages = context.ResponseMessages?.ToArray() ?? [];
        if (responseMessages.Length == 0)
        {
            return;
        }

        await this.store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = agentSessionId,
                    AgentSessionJson = await this.SerializeSessionAsync(context.Session, cancellationToken).ConfigureAwait(false),
                    AgentDefinitionJson = this.agentDefinitionJson,
                },
                NewMessages = responseMessages,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BsonDocument> SerializeSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var serializeSession = this.serializeSession
            ?? throw new InvalidOperationException("Session serializer was not configured before persistence was used.");

        return await serializeSession(session, cancellationToken).ConfigureAwait(false);
    }
}
