using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A <see cref="TaskScheduler"/> that executes all queued work on a captured
/// <see cref="System.Threading.SynchronizationContext"/> (in the GUI, the UI thread's dispatcher
/// context) and — unlike the scheduler returned by
/// <see cref="TaskScheduler.FromCurrentSynchronizationContext"/> — exposes that context, so
/// components such as <see cref="AgentChat"/> can <em>verify</em> that callers are on the
/// foreground context instead of silently accepting off-thread work (issue #909).
/// </summary>
public sealed class SynchronizationContextTaskScheduler : TaskScheduler
{
    public SynchronizationContextTaskScheduler(SynchronizationContext synchronizationContext)
    {
        ArgumentNullException.ThrowIfNull(synchronizationContext);
        this.SynchronizationContext = synchronizationContext;
    }

    /// <summary>The synchronization context all queued work is posted to.</summary>
    public SynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Whether the calling thread currently has this scheduler's
    /// <see cref="SynchronizationContext"/> installed.
    /// </summary>
    public bool IsOnSynchronizationContext =>
        System.Threading.SynchronizationContext.Current == this.SynchronizationContext;

    public override int MaximumConcurrencyLevel => 1;

    /// <summary>
    /// Creates a scheduler over <see cref="SynchronizationContext.Current"/>.
    /// Throws when the calling thread has no synchronization context installed.
    /// </summary>
    public static SynchronizationContextTaskScheduler FromCurrent() =>
        new(System.Threading.SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                $"{nameof(SynchronizationContextTaskScheduler)}.{nameof(FromCurrent)} requires a current SynchronizationContext; call it on the UI thread."));

    protected override void QueueTask(Task task) =>
        this.SynchronizationContext.Post(state => this.TryExecuteTask((Task)state!), task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        this.IsOnSynchronizationContext && this.TryExecuteTask(task);

    protected override IEnumerable<Task>? GetScheduledTasks() => null;
}
