namespace Phantom.Workspaces.Llm;

public sealed record AgentRuntimeQueueCheckpoint
{
    public required AgentInputQueuesSnapshot InputQueues { get; init; }

    public required bool HasInterruptedWork { get; init; }
}
