using System.Runtime.CompilerServices;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

internal enum AgentChatInterruptOutcome
{
    None,
    Pending,
    Succeeded,
    Canceled,
    Failed,
}

internal readonly record struct AgentChatInterruptSnapshot(
    bool IsPending,
    long Generation,
    AgentChatInterruptOutcome Outcome);

/// <summary>Coordinates one owner-acknowledged interrupt operation for a chat.</summary>
internal sealed class AgentChatInterruptState
{
    private static readonly ConditionalWeakTable<IAgentChat, AgentChatInterruptState> states = new();

    private readonly object sync = new();
    private Task? pendingTask;
    private long generation;
    private AgentChatInterruptOutcome outcome;

    internal static AgentChatInterruptState For(IAgentChat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return states.GetValue(chat, static _ => new AgentChatInterruptState());
    }

    internal event EventHandler? StateChanged;

    internal AgentChatInterruptSnapshot Snapshot
    {
        get
        {
            lock (this.sync)
            {
                return new AgentChatInterruptSnapshot(
                    this.pendingTask is not null,
                    this.generation,
                    this.outcome);
            }
        }
    }

    internal bool IsPending => this.Snapshot.IsPending;

    internal Task InterruptAsync(
        Func<CancellationToken, Task> interruptAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interruptAsync);

        TaskCompletionSource completion;
        long ownedGeneration;
        lock (this.sync)
        {
            if (this.pendingTask is { } pending)
            {
                return pending;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.pendingTask = completion.Task;
            ownedGeneration = ++this.generation;
            this.outcome = AgentChatInterruptOutcome.Pending;
        }

        this.PublishStateChanged();
        _ = this.ExecuteOwnedAsync(completion, ownedGeneration, interruptAsync, cancellationToken);
        return completion.Task;
    }

    private async Task ExecuteOwnedAsync(
        TaskCompletionSource completion,
        long ownedGeneration,
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

        var outcome = failure switch
        {
            OperationCanceledException => AgentChatInterruptOutcome.Canceled,
            not null => AgentChatInterruptOutcome.Failed,
            null => AgentChatInterruptOutcome.Succeeded,
        };

        try
        {
            lock (this.sync)
            {
                if (ReferenceEquals(this.pendingTask, completion.Task)
                    && this.generation == ownedGeneration)
                {
                    this.outcome = outcome;
                }
            }

            // Publish failure before releasing ownership so an early retry still coalesces.
            this.PublishStateChanged();
        }
        finally
        {
            try
            {
                var released = false;
                lock (this.sync)
                {
                    if (ReferenceEquals(this.pendingTask, completion.Task)
                        && this.generation == ownedGeneration)
                    {
                        this.pendingTask = null;
                        released = true;
                    }
                }

                if (released)
                {
                    this.PublishStateChanged();
                }
            }
            finally
            {
                // This task acknowledges cleanup and publication, not just outcome selection.
                // A subscriber may already own a newer generation: settle only our captured task.
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
    }

    private void PublishStateChanged() =>
        InterruptNotifications.Publish(
            this.StateChanged,
            handler => handler(this, EventArgs.Empty));
}
