using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

public interface IRunningAgentChatTable
{
    Task<RunningAgentChatLease> AcquireAsync(string sessionKey, Func<Task<AgentChat>> factory);
}
