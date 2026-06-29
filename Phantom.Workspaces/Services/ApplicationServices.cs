using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Services;

public sealed class ApplicationServices
{
    public ApplicationServices(
        IRunningAgentChatTable runningAgentChats,
        IAgentPersistenceStoreCache agentPersistenceStoreCache,
        IUpdateController? updateController = null)
    {
        this.RunningAgentChats = runningAgentChats;
        this.AgentPersistenceStoreCache = agentPersistenceStoreCache;
        this.UpdateController = updateController;
    }

    public IRunningAgentChatTable RunningAgentChats { get; }

    public IAgentPersistenceStoreCache AgentPersistenceStoreCache { get; }

    public IUpdateController? UpdateController { get; }
}
