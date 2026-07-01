namespace Phantom.Workspaces.Services;

/// <summary>
/// Represents an active agent chat session tracked by <see cref="IRunningAgentChatTable"/>.
/// </summary>
public sealed class RunningAgentChat
{
    private readonly RunningAgentChatTable table;

    /// <summary>The session key used to identify and share the underlying <see cref="Llm.AgentChat"/>.</summary>
    public string SessionKey { get; }

    /// <summary>The display name of the agent entity that owns this session.</summary>
    public string EntityName { get; }

    /// <summary>
    /// The entity store ID (UUID) of the agent-session entity, if available.
    /// Used to navigate to the entity when no tab is open for this session.
    /// </summary>
    public string? EntityId { get; }

    internal RunningAgentChat(RunningAgentChatTable table, string sessionKey, string entityName, string? entityId = null)
    {
        this.table = table;
        this.SessionKey = sessionKey;
        this.EntityName = entityName;
        this.EntityId = entityId;
    }

    /// <summary>
    /// Acquires a new lease on this running session's <see cref="Llm.AgentChat"/>.
    /// Dispose the returned lease when done to release the reference.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync()
        => this.table.AcquireLeaseForExistingSessionAsync(this.SessionKey);
}
