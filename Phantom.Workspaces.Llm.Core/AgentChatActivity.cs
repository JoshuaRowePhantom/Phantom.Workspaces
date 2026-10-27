using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public static class AgentChatActivity
{
    public static bool HasPendingWork(IAgentChat chat)
        => !chat.HistoryPopulated.IsCompletedSuccessfully
            || chat is AgentChat local && !local.Initialization.IsCompletedSuccessfully
            || chat.RunningItems.Count > 0
            || chat.Modals.Count > 0
            || chat.InputQueues.Snapshot.Queues.Any(queue => !queue.Items.IsEmpty)
            || chat.SubAgents.Any(HasPendingDescendant);

    private static bool HasPendingDescendant(IRunningSubAgent child)
        => child.CompletionState is AgentChatCompletionState.Running or AgentChatCompletionState.Unknown
            || child is IAgentChat chat && HasPendingWork(chat)
            || child is SubAgent { AgentChat: { } local } && HasPendingWork(local)
            || child.SubAgents.Any(HasPendingDescendant);
}
