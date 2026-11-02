namespace Phantom.Workspaces.Llm.Remote;

public sealed record AgentSessionRuntimeIdentity
{
    public required string AgentSessionId { get; init; }
    public required string OwningProfileEntityId { get; init; }
    public required long OwnershipGeneration { get; init; }
    public required RuntimeEpoch RuntimeEpoch { get; init; }
}

public sealed record AgentSessionOwnerRestartRequest
{
    public required AgentSessionRuntimeIdentity ExpectedRuntime { get; init; }
    public required Guid CommandId { get; init; }
}

public enum AgentSessionOwnerRestartState
{
    Committed,
    Stopped,
    Succeeded,
    Blocked,
    TargetChanged,
}

public sealed record AgentSessionOwnerRestartResult
{
    public required Guid CommandId { get; init; }
    public required AgentSessionOwnerRestartState State { get; init; }
    public AgentSessionRuntimeIdentity? ReplacementRuntime { get; init; }
    public string? Message { get; init; }
}
