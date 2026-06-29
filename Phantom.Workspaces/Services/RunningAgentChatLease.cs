using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

public sealed class RunningAgentChatLease : IAsyncDisposable
{
    private readonly RunningAgentChatTable table;
    private readonly string sessionKey;
    private int disposed;

    public AgentChat AgentChat { get; }

    internal RunningAgentChatLease(RunningAgentChatTable table, string sessionKey, AgentChat agentChat)
    {
        this.table = table;
        this.sessionKey = sessionKey;
        this.AgentChat = agentChat;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref this.disposed, 1, 0) != 0)
        {
            return;
        }

        await this.table.ReleaseAsync(this.sessionKey, this);
    }
}
