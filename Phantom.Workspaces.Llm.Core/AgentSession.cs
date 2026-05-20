using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm;

public sealed class AgentSession
{
    private readonly object syncLock = new();
    private LlmSession llmSession;

    public AgentSession(
        LlmSession llmSession,
        IAgentExecutionEnvironment executionEnvironment,
        ILlmProvider llmProvider)
    {
        this.llmSession = llmSession ?? throw new ArgumentNullException(nameof(llmSession));
        this.ExecutionEnvironment = executionEnvironment ?? throw new ArgumentNullException(nameof(executionEnvironment));
        this.LlmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
    }

    public IAgentExecutionEnvironment ExecutionEnvironment { get; }

    public ILlmProvider LlmProvider { get; }

    public LlmSession LlmSession
    {
        get
        {
            lock (this.syncLock)
            {
                return this.llmSession;
            }
        }
    }

    public static AgentSession Create(
        LlmSession llmSession,
        IAgentExecutionEnvironment executionEnvironment,
        ILlmProvider llmProvider)
    {
        return new AgentSession(llmSession, executionEnvironment, llmProvider);
    }

    public async IAsyncEnumerable<AgentSessionUpdate> Process(
        IAsyncEnumerable<SessionInputEvent> inputEvents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputEvents);

        var inputEnumerator = inputEvents.GetAsyncEnumerator(cancellationToken);
        var pendingInputs = new Queue<SessionInputEvent>();
        IAsyncEnumerator<LlmStreamEvent>? providerEnumerator = null;
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
                    if (nextInput.LlmEvents.Length > 0)
                    {
                        this.AppendEvents(nextInput.LlmEvents);
                        yield return new AgentSessionUpdate
                        {
                            LlmSession = this.LlmSession,
                            LlmStreamingEvent = null,
                        };
                        (providerEnumerator, providerCts) = this.StartProvider(cancellationToken);
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

                    var hasStreamEventAfterInput = false;
                    try
                    {
                        hasStreamEventAfterInput = await moveNextProviderTask;
                    }
                    catch (OperationCanceledException)
                    {
                        hasStreamEventAfterInput = false;
                    }

                    if (hasStreamEventAfterInput)
                    {
                        var streamEvent = providerEnumerator.Current;
                        this.ApplyStreamEvent(streamEvent);
                        yield return new AgentSessionUpdate
                        {
                            LlmSession = this.LlmSession,
                            LlmStreamingEvent = streamEvent,
                        };
                        continue;
                    }

                    if (interruptRequested || nextInputTask.IsCompletedSuccessfully && !nextInputTask.Result)
                    {
                        await providerEnumerator.DisposeAsync();
                        providerEnumerator = null;
                        providerCts?.Dispose();
                        providerCts = null;
                    }

                    continue;
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
                    var streamEvent = providerEnumerator.Current;
                    this.ApplyStreamEvent(streamEvent);
                    yield return new AgentSessionUpdate
                    {
                        LlmSession = this.LlmSession,
                        LlmStreamingEvent = streamEvent,
                    };
                    continue;
                }

                await providerEnumerator.DisposeAsync();
                providerEnumerator = null;
                providerCts?.Dispose();
                providerCts = null;

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
                await providerEnumerator.DisposeAsync();
            }

            providerCts?.Dispose();
        }
    }

    private (IAsyncEnumerator<LlmStreamEvent> Enumerator, CancellationTokenSource Cts) StartProvider(
        CancellationToken cancellationToken)
    {
        var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var conversation = this.GetCurrentConversation();
        var providerEnumerator = this.LlmProvider
            .StreamAsync(conversation, providerCts.Token)
            .GetAsyncEnumerator(providerCts.Token);
        return (providerEnumerator, providerCts);
    }

    private LlmConversation GetCurrentConversation()
    {
        lock (this.syncLock)
        {
            return this.llmSession.Conversations.Count > 0
                ? this.llmSession.Conversations[^1]
                : LlmConversation.Create();
        }
    }

    private void AppendEvents(
        IEnumerable<LlmEvent> events)
    {
        lock (this.syncLock)
        {
            this.llmSession = LlmSessionBuilder
                .FromSession(this.llmSession)
                .AddEvents(events)
                .Build();
        }
    }

    private void ApplyStreamEvent(
        LlmStreamEvent streamEvent)
    {
        lock (this.syncLock)
        {
            this.llmSession = LlmSessionBuilder
                .FromSession(this.llmSession)
                .AddStreamEvent(streamEvent)
                .Build();
        }
    }
}
