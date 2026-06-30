using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

public sealed class RunningAgentChatLease : IAsyncDisposable
{
    private readonly RunningAgentChatTable table;
    private int disposed;

    public string SessionKey { get; }

    public AgentChat AgentChat { get; }

    internal RunningAgentChatLease(RunningAgentChatTable table, string sessionKey, AgentChat agentChat)
    {
        this.table = table;
        this.SessionKey = sessionKey;
        this.AgentChat = agentChat;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref this.disposed, 1, 0) != 0)
        {
            return;
        }

        await this.table.ReleaseAsync(this.SessionKey, this);
    }
}
