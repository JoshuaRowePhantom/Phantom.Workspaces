using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.ViewModels;

internal static class AgentSessionCloseActivity
{
    internal static bool HasPendingWork(IAgentChat chat)
        => !chat.HistoryPopulated.IsCompletedSuccessfully
            || chat.RunningItems.Count > 0
            || chat.Modals.Count > 0
            || chat.InputQueues.Snapshot.Queues.Any(queue => !queue.Items.IsEmpty)
            || chat.SubAgents.Any(HasPendingDescendant);

    private static bool HasPendingDescendant(IRunningSubAgent child)
        => child.CompletionState is AgentChatCompletionState.Running or AgentChatCompletionState.Unknown
            || child is IAgentChat chat && HasPendingWork(chat)
            || child.SubAgents.Any(HasPendingDescendant);
}
