using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed class AgentInputQueueManager
{
    public sealed record QueuePublishedEventArgs
    {
        public required ImmutableList<ChatMessage> Messages { get; init; }

        public bool InterruptCurrentResponse { get; init; }
    }

    private readonly object syncLock = new();
    private readonly List<AgentInputQueue> inputQueues;
    private readonly Channel<SessionInputEvent> inputEvents;
    private readonly ChatClientAgent agent;
    private bool isBusy;

    public event EventHandler<QueuePublishedEventArgs>? QueuePublished;

    public AgentInputQueueManager(
        ChatClientAgent agent)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.ImmediateQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = int.MaxValue,
                Immediacy = AgentInputQueueImmediacy.Immediate,
            });
        this.inputQueues = [this.ImmediateQueue];
        this.inputEvents = Channel.CreateUnbounded<SessionInputEvent>();
        this.ImmediateQueue.Changed += this.OnQueueChanged;
    }

    public ChatClientAgent Agent => this.agent;

    public AgentInputQueue ImmediateQueue { get; }

    public bool IsBusy => this.isBusy;

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

    public async IAsyncEnumerable<AgentResponseUpdate> Process(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var agentSession = await this.agent.CreateSessionAsync(cancellationToken);

        var inputEnumerator = this.inputEvents.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var pendingInputs = new Queue<SessionInputEvent>();
        IAsyncEnumerator<AgentResponseUpdate>? providerEnumerator = null;
        CancellationTokenSource? providerCts = null;
        var nextInputTask = inputEnumerator.MoveNextAsync().AsTask();

        try
        {
            while (true)
            {
                if (providerEnumerator is null && pendingInputs.Count == 0)
                {
                    var hasNextInput = await nextInputTask;
                    if (!hasNextInput)
                    {
                        yield break;
                    }

                    pendingInputs.Enqueue(inputEnumerator.Current);
                    nextInputTask = inputEnumerator.MoveNextAsync().AsTask();
                }

                if (providerEnumerator is null && pendingInputs.Count > 0)
                {
                    var nextInput = pendingInputs.Dequeue();
                    if (nextInput.Messages.Length > 0)
                    {
                        this.isBusy = true;
                        (providerEnumerator, providerCts) = this.StartRun(nextInput.Messages, agentSession, cancellationToken);
                    }

                    continue;
                }

                if (providerEnumerator is null)
                {
                    continue;
                }

                var moveNextProviderTask = providerEnumerator.MoveNextAsync().AsTask();
                var completedTask = await Task.WhenAny(moveNextProviderTask, nextInputTask);

                if (completedTask == nextInputTask)
                {
                    var interruptRequested = false;
                    var hasNextInput = await nextInputTask;
                    if (hasNextInput)
                    {
                        var input = inputEnumerator.Current;
                        if (input.InterruptCurrentResponse)
                        {
                            interruptRequested = true;
                            providerCts?.Cancel();
                        }

                        pendingInputs.Enqueue(input);
                        nextInputTask = inputEnumerator.MoveNextAsync().AsTask();
                    }
                    else
                    {
                        nextInputTask = Task.FromResult(false);
                    }

                    var hasStreamEvent = false;
                    try
                    {
                        hasStreamEvent = await moveNextProviderTask;
                    }
                    catch (OperationCanceledException)
                    {
                        hasStreamEvent = false;
                    }

                    if (hasStreamEvent)
                    {
                        yield return providerEnumerator.Current;
                        continue;
                    }

                    if (interruptRequested || (nextInputTask.IsCompletedSuccessfully && !nextInputTask.Result))
                    {
                        await DisposeProviderEnumeratorAsync(providerEnumerator);
                        providerEnumerator = null;
                        providerCts?.Dispose();
                        providerCts = null;
                        this.isBusy = false;
                        this.ServiceQueues(false);
                    }

                    continue;
                }

                var hasResponse = false;
                try
                {
                    hasResponse = await moveNextProviderTask;
                }
                catch (OperationCanceledException)
                {
                    hasResponse = false;
                }

                if (hasResponse)
                {
                    yield return providerEnumerator.Current;
                    continue;
                }

                await DisposeProviderEnumeratorAsync(providerEnumerator);
                providerEnumerator = null;
                providerCts?.Dispose();
                providerCts = null;
                this.isBusy = false;
                this.ServiceQueues(false);

                if (nextInputTask.IsCompletedSuccessfully
                    && !nextInputTask.Result
                    && pendingInputs.Count == 0)
                {
                    yield break;
                }
            }
        }
        finally
        {
            try
            {
                await inputEnumerator.DisposeAsync();
            }
            catch (NotSupportedException)
            {
            }

            if (providerEnumerator is not null)
            {
                await DisposeProviderEnumeratorAsync(providerEnumerator);
            }
            providerCts?.Dispose();
            providerCts?.Dispose();
            this.isBusy = false;
        }

    }

    private static async Task DisposeProviderEnumeratorAsync(
        IAsyncEnumerator<AgentResponseUpdate> providerEnumerator)
    {
        try
        {
            await providerEnumerator.DisposeAsync();
        }
        catch (NotSupportedException)
        {
        }
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
                queue.Changed += this.OnQueueChanged;
            }
        }
    }

    public bool UnregisterInputQueue(
        AgentInputQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        lock (this.syncLock)
        {
            if (ReferenceEquals(queue, this.ImmediateQueue))
            {
                return false;
            }

            var removed = this.inputQueues.Remove(queue);
            if (removed)
            {
                queue.Changed -= this.OnQueueChanged;
            }

            return removed;
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
        if (this.isBusy)
        {
            return 0;
        }

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

    private (IAsyncEnumerator<AgentResponseUpdate> Enumerator, CancellationTokenSource Cts) StartRun(
        ChatMessage[] messages,
        AgentSession agentSession,
        CancellationToken cancellationToken)
    {
        var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var providerEnumerator = this.agent
            .RunStreamingAsync(messages, agentSession, cancellationToken: providerCts.Token)
            .GetAsyncEnumerator(providerCts.Token);
        return (providerEnumerator, providerCts);
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

        this.QueuePublished?.Invoke(
            this,
            new QueuePublishedEventArgs
            {
                Messages = drained,
                InterruptCurrentResponse = interruptCurrentResponse,
            });

        return drained.Count;
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        this.ServiceQueues(false);
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

            var expectedItems = existingItems;
            if (queue.Update(ref expectedItems, ImmutableList<ChatMessage>.Empty))
            {
                return existingItems;
            }
        }
    }

    private record SessionInputEvent
    {
        public required ChatMessage[] Messages { get; init; }

        public bool InterruptCurrentResponse { get; init; }
    }
}