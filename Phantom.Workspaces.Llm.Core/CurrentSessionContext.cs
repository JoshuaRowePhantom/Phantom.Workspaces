using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Remote;

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
    private readonly string owningProfileEntityId = string.Empty;
    private readonly long ownershipGeneration;

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

    /// <summary>
    /// Persisted owning profile entity id. Attachment peers preserve this host identity rather
    /// than replacing it with their own profile.
    /// </summary>
    public required string OwningProfileEntityId
    {
        get => this.owningProfileEntityId;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "OwningProfileEntityId must be non-blank.",
                    nameof(value));
            }
            this.owningProfileEntityId = value;
        }
    }

    /// <summary>
    /// #1485: monotonically increasing ownership generation. Bumped when ownership transfers to a
    /// new host. Must be nonnegative.
    /// </summary>
    public required long OwnershipGeneration
    {
        get => this.ownershipGeneration;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "OwnershipGeneration must be nonnegative.");
            }
            this.ownershipGeneration = value;
        }
    }

    /// <summary>
    /// Runtime epoch assigned once a host runtime exists. A context may be composed before startup,
    /// in which case this remains null.
    /// </summary>
    public RuntimeEpoch? RuntimeEpoch { get; init; }
}
