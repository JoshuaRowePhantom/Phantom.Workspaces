using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// The live host context for the agent session currently running, supplied by the Phantom.Workspaces
/// host when it starts or resumes a session. The profile and user are whatever the host instance
/// running the session right now uses, so a session resumed on a different machine reports that
/// host's current profile and user rather than the profile stored on the session entity.
/// </summary>
public sealed record CurrentSessionContext
{
    private readonly string agentSessionId = string.Empty;

    /// <summary>The running agent session identifier, stable across resumes.</summary>
    public required string AgentSessionId
    {
        get => this.agentSessionId;
        init
        {
            // #1485: publisher-side rejection of blank session identifiers keeps the host
            // identity non-ambiguous during resume/attach.
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("AgentSessionId must be non-blank.", nameof(value));
            }
            this.agentSessionId = value;
        }
    }

    /// <summary>The host's current user-computer-profile entity, or null when the host could not resolve one.</summary>
    public EntitySnapshot? UserComputerProfile { get; init; }

    /// <summary>The host's current user entity, or null when the host could not resolve one.</summary>
    public EntitySnapshot? User { get; init; }

    /// <summary>The host's current computer entity, or null when the host could not resolve one.</summary>
    public EntitySnapshot? Computer { get; init; }

    /// <summary>An entity-name reference to the agent-definition the host is currently running, or null when unknown.</summary>
    public EntityName? AgentDefinitionReference { get; init; }
}
