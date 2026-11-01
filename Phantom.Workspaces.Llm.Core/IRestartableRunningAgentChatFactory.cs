using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public interface IRestartableRunningAgentChatFactory
{
    Task<RunningAgentChatLease> RestartExactAsync(
        AgentSessionId sessionId,
        IAgentChat expected,
        AgentServices? services,
        Func<AgentRuntimeQueueCheckpoint, CancellationToken, ValueTask> persistCheckpointAsync,
        CancellationToken cancellationToken = default);
}
