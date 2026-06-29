using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed record QueueImmediacyOption(AgentInputQueueImmediacy Value, string Label)
{
    public static readonly IReadOnlyList<QueueImmediacyOption> All =
    [
        new QueueImmediacyOption(AgentInputQueueImmediacy.Immediate, "immediate"),
        new QueueImmediacyOption(AgentInputQueueImmediacy.Queue, "queued"),
        new QueueImmediacyOption(AgentInputQueueImmediacy.Held, "held"),
    ];
}
