using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed class AgentInputQueueManager
{
    private readonly object syncLock = new();
    private readonly List<AgentInputQueue> inputQueues;
    private readonly Channel<SessionInputEvent> inputEvents;

    public AgentInputQueueManager(
        AgentSession session)
    {
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.ImmediateQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = int.MaxValue,
                Immediacy = AgentInputQueueImmediacy.Immediate,
            });
        this.inputQueues = [this.ImmediateQueue];
        this.inputEvents = Channel.CreateUnbounded<SessionInputEvent>();
    }

    public AgentSession Session { get; }

    public AgentInputQueue ImmediateQueue { get; }

    public IReadOnlyList<AgentInputQueue> InputQueue
    {
        get
        {
            lock (this.syncLock)
            {
                return this.inputQueues.ToArray();
            }
        }
    }

    public IAsyncEnumerable<AgentSessionUpdate> Process(
        CancellationToken cancellationToken = default)
    {
        return this.Session.Process(
            this.inputEvents.Reader.ReadAllAsync(cancellationToken),
            cancellationToken);
    }

    public void Complete()
    {
        this.inputEvents.Writer.TryComplete();
    }

    public void RequestInterrupt()
    {
        if (!this.inputEvents.Writer.TryWrite(
                new SessionInputEvent
                {
                    Messages = [],
                    InterruptCurrentResponse = true,
                }))
        {
            throw new InvalidOperationException("Unable to publish interrupt signal to session processor.");
        }
    }

    public void RegisterInputQueue(
        AgentInputQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        lock (this.syncLock)
        {
            if (!this.inputQueues.Contains(queue))
            {
                this.inputQueues.Add(queue);
            }
        }
    }

    public ImmutableList<ChatMessage> Enqueue(
        AgentInputQueue queue,
        IEnumerable<ChatMessage> messages,
        bool interrupt = false)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(messages);

        this.RegisterInputQueue(queue);
        var queuedItems = queue.Enqueue(messages);
        if (interrupt)
        {
            this.Interrupt(queue);
        }
        else if (queue.Immediacy == AgentInputQueueImmediacy.Immediate)
        {
            this.PublishQueue(queue, interruptCurrentResponse: false);
        }

        return queuedItems;
    }

    public int ServiceQueues(
        bool modelTurnIncludedToolCalls)
    {
        IReadOnlyList<AgentInputQueue> queues;
        lock (this.syncLock)
        {
            queues = this.inputQueues
                .Where(queue => queue.Items.Count > 0)
                .Where(queue => IsEligibleForService(queue, modelTurnIncludedToolCalls))
                .OrderByDescending(queue => queue.Priority)
                .ToArray();
        }

        var publishedEventCount = 0;
        foreach (var queue in queues)
        {
            var published = this.PublishQueue(queue, interruptCurrentResponse: false);
            publishedEventCount += published;
        }

        return publishedEventCount;
    }

    public int Interrupt(
        AgentInputQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return this.PublishQueue(queue, interruptCurrentResponse: true);
    }

    private static bool IsEligibleForService(
        AgentInputQueue queue,
        bool modelTurnIncludedToolCalls)
    {
        return queue.Immediacy switch
        {
            AgentInputQueueImmediacy.Immediate => true,
            AgentInputQueueImmediacy.Queue => !modelTurnIncludedToolCalls,
            AgentInputQueueImmediacy.Held => false,
            _ => false,
        };
    }

    private int PublishQueue(
        AgentInputQueue queue,
        bool interruptCurrentResponse)
    {
        var drained = this.DrainQueue(queue);
        if (drained.Count == 0)
        {
            return 0;
        }

        if (!this.inputEvents.Writer.TryWrite(
                new SessionInputEvent
                {
                    Messages = drained.ToArray(),
                    InterruptCurrentResponse = interruptCurrentResponse,
                }))
        {
            throw new InvalidOperationException("Unable to publish queue items to session processor.");
        }

        return drained.Count;
    }

    private ImmutableList<ChatMessage> DrainQueue(
        AgentInputQueue queue)
    {
        while (true)
        {
            var existingItems = queue.Items;
            if (existingItems.Count == 0)
            {
                return existingItems;
            }

            if (queue.Update(existingItems, ImmutableList<ChatMessage>.Empty))
            {
                return existingItems;
            }
        }
    }
}

