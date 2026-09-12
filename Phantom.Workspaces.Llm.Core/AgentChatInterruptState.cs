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

        this.StateChanged?.Invoke(this, EventArgs.Empty);
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

        lock (this.sync)
        {
            if (!ReferenceEquals(this.pendingTask, completion.Task)
                || this.generation != ownedGeneration)
            {
                return;
            }

            this.outcome = outcome;
        }

        // Publish the complete outcome while ownership is still held. Subscribers can
        // update every surface before any entry point is allowed to begin a retry.
        this.StateChanged?.Invoke(this, EventArgs.Empty);

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

        lock (this.sync)
        {
            if (!ReferenceEquals(this.pendingTask, completion.Task)
                || this.generation != ownedGeneration)
            {
                return;
            }

            this.pendingTask = null;
        }

        this.StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
