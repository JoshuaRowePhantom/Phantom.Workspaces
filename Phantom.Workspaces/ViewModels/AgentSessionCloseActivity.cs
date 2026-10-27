using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.ViewModels;

internal static class AgentSessionCloseActivity
{
    internal static bool HasPendingWork(IAgentChat chat)
        => AgentChatActivity.HasPendingWork(chat);
}
