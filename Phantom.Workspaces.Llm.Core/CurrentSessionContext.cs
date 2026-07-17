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
    /// <summary>The running agent session identifier, stable across resumes.</summary>
    public required string AgentSessionId { get; init; }

    /// <summary>The host's current user-computer-profile entity, or null when the host could not resolve one.</summary>
    public EntitySnapshot? UserComputerProfile { get; init; }

    /// <summary>The host's current user entity, or null when the host could not resolve one.</summary>
    public EntitySnapshot? User { get; init; }

    /// <summary>The host's current computer entity, or null when the host could not resolve one.</summary>
    public EntitySnapshot? Computer { get; init; }

    /// <summary>An entity-name reference to the agent-definition the host is currently running, or null when unknown.</summary>
    public EntityName? AgentDefinitionReference { get; init; }
}
