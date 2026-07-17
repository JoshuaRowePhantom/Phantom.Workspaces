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

    ~RunningAgentChatLease()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            ObserveDisposal(_onDispose());
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return ValueTask.CompletedTask;
        }

        GC.SuppressFinalize(this);
        return _onDispose();
    }

    // Ensures the fire-and-forget disposal launched from the finalizer can never leave an
    // unobserved Task exception behind (which would be rethrown by the finalizer thread and
    // crash the process). The exception is observed and swallowed.
    private static void ObserveDisposal(ValueTask disposeTask)
    {
        if (disposeTask.IsCompletedSuccessfully)
        {
            return;
        }

        _ = AwaitAndSwallowAsync(disposeTask);

        static async Task AwaitAndSwallowAsync(ValueTask task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Intentionally swallowed: nothing can meaningfully handle a disposal fault raised
                // during finalization, and letting it escape would crash the finalizer thread.
            }
        }
    }
}
