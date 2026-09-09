using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public sealed class RunningAgentChatLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _onDispose;
    private readonly Func<ValueTask>? _afterDispose;
    private int _disposed;

    public AgentSessionId SessionId { get; }

    public IAgentChat AgentChat { get; }

    /// <summary>
    /// Temporary local-engine compatibility accessor. New UI code consumes <see cref="AgentChat"/>.
    /// </summary>
    public AgentChat LocalAgentChat { get; }

    internal RunningAgentChatLease(
        AgentSessionId sessionId,
        AgentChat agentChat,
        Func<ValueTask> onDispose,
        Func<ValueTask>? afterDispose = null)
    {
        SessionId = sessionId;
        AgentChat = agentChat;
        LocalAgentChat = agentChat;
        _onDispose = onDispose;
        _afterDispose = afterDispose;
    }

    ~RunningAgentChatLease()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            ObserveDisposal(this.DisposeCoreAsync());
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return ValueTask.CompletedTask;
        }

        GC.SuppressFinalize(this);
        return DisposeCoreAsync();
    }

    private async ValueTask DisposeCoreAsync()
    {
        await _onDispose().ConfigureAwait(false);
        if (_afterDispose is not null)
        {
            await _afterDispose().ConfigureAwait(false);
        }
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
