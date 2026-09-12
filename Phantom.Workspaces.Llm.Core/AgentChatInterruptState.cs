using System.Runtime.CompilerServices;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>Coordinates one owner-acknowledged interrupt operation for a chat.</summary>
internal sealed class AgentChatInterruptState
{
    private static readonly ConditionalWeakTable<IAgentChat, AgentChatInterruptState> states = new();

    private readonly object sync = new();
    private Task? pendingTask;

    internal static AgentChatInterruptState For(IAgentChat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return states.GetValue(chat, static _ => new AgentChatInterruptState());
    }

    internal event EventHandler? PendingChanged;

    internal bool IsPending
    {
        get
        {
            lock (this.sync)
            {
                return this.pendingTask is not null;
            }
        }
    }

    internal Task InterruptAsync(
        Func<CancellationToken, Task> interruptAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interruptAsync);

        TaskCompletionSource completion;
        lock (this.sync)
        {
            if (this.pendingTask is { } pending)
            {
                return pending;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.pendingTask = completion.Task;
        }

        this.PendingChanged?.Invoke(this, EventArgs.Empty);
        _ = this.ExecuteOwnedAsync(completion, interruptAsync, cancellationToken);
        return completion.Task;
    }

    private async Task ExecuteOwnedAsync(
        TaskCompletionSource completion,
        Func<CancellationToken, Task> interruptAsync,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await interruptAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (this.sync)
            {
                if (ReferenceEquals(this.pendingTask, completion.Task))
                {
                    this.pendingTask = null;
                }
            }

            this.PendingChanged?.Invoke(this, EventArgs.Empty);
        }

        if (failure is OperationCanceledException canceled)
        {
            completion.TrySetCanceled(canceled.CancellationToken);
        }
        else if (failure is not null)
        {
            completion.TrySetException(failure);
        }
        else
        {
            completion.TrySetResult();
        }
    }
}
