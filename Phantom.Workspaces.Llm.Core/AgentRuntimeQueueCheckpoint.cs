namespace Phantom.Workspaces.Llm;

public sealed record AgentRuntimeQueueCheckpoint
{
    public required AgentInputQueuesSnapshot InputQueues { get; init; }

    public required bool HasInterruptedWork { get; init; }

    public string? ModelId { get; init; }

    public IReadOnlyDictionary<string, bool> ToolEnablement { get; init; } =
        new Dictionary<string, bool>();

    public IReadOnlyDictionary<string, string> ParameterValues { get; init; } =
        new Dictionary<string, string>();
}
