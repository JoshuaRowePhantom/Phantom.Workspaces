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
    private readonly string? owner;
    private readonly long ownershipGeneration;
    private readonly long runtimeEpoch;

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
    /// #1485: owning host identity. Set by the host that currently owns the session. An attachment
    /// peer (viewer) leaves this member unmodified so remote proxies preserve the owning host's
    /// identity rather than replacing it with the viewer's.
    /// </summary>
    public string? Owner
    {
        get => this.owner;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Owner must be null or non-blank.", nameof(value));
            }

            this.owner = value;
        }
    }

    /// <summary>
    /// Persisted owning profile entity id. This is the property-based remote-session name; the
    /// legacy <see cref="Owner"/> alias remains for source compatibility.
    /// </summary>
    public string? OwningProfileEntityId
    {
        get => this.owner;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "OwningProfileEntityId must be null or non-blank.",
                    nameof(value));
            }
            this.owner = value;
        }
    }

    /// <summary>
    /// #1485: monotonically increasing ownership generation. Bumped when ownership transfers to a
    /// new host. Must be nonnegative.
    /// </summary>
    public long OwnershipGeneration
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
    /// #1485: runtime epoch. Bumped whenever the host restarts or resumes the session process.
    /// Must be nonnegative.
    /// </summary>
    public long RuntimeEpoch
    {
        get => this.runtimeEpoch;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "RuntimeEpoch must be nonnegative.");
            }
            this.runtimeEpoch = value;
        }
    }
}
