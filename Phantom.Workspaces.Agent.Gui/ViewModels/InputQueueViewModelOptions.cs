using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed record InputQueueViewModelOptions
{
    public required IAgentChat AgentChat { get; init; }
    public string? DefaultQueueId { get; init; }
    public string? HiddenBuiltInQueueId { get; init; }
}

internal sealed record RemoveQueueItemContentRequest
{
    public required string QueueId { get; init; }
    public required string ItemId { get; init; }
    public required int ContentIndex { get; init; }
}
