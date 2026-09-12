using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.Services;

public sealed record PersistedAgentSessionRuntimeIntent
{
    private string agentSessionId = "";
    private string owningProfileEntityId = "";
    private long ownershipGeneration;

    public required string AgentSessionId
    {
        get => this.agentSessionId;
        init => this.agentSessionId = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Agent session ID must not be empty.", nameof(value));
    }

    public required string OwningProfileEntityId
    {
        get => this.owningProfileEntityId;
        init => this.owningProfileEntityId = Guid.TryParse(value, out _)
            ? value
            : throw new ArgumentException("Owning profile entity ID must be a UUID.", nameof(value));
    }

    public required long OwnershipGeneration
    {
        get => this.ownershipGeneration;
        init => this.ownershipGeneration = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public required ExecutorBindings ExecutorBindings { get; init; }

    public string? TrustProfileReference { get; init; } = null;

    public string? ExpectedTrustProfileRevision { get; init; } = null;

    public bool ContinueInBackground { get; init; } = false;
}
