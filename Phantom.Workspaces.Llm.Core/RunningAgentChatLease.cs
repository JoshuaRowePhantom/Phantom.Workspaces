using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public sealed class RunningAgentChatLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _onDispose;
    private int _disposed;

    public AgentSessionId SessionId { get; }

    public AgentChat AgentChat { get; }

    internal RunningAgentChatLease(AgentSessionId sessionId, AgentChat agentChat, Func<ValueTask> onDispose)
    {
        SessionId = sessionId;
        AgentChat = agentChat;
        _onDispose = onDispose;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _onDispose();
    }
}
