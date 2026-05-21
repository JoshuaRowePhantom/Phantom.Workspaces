using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Registry of tools that can be executed during agent sessions.
/// </summary>
public interface IToolRegistry
{
    Task<string> ExecuteToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default);
}

public sealed class AgentSession
{
    private readonly object syncLock = new();
    private List<ChatMessage> messages = new();
    private readonly IToolRegistry? toolRegistry;

    public AgentSession(
        IChatClient chatClient,
        IToolRegistry? toolRegistry = null)
    {
        this.ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        this.toolRegistry = toolRegistry;
    }

    public IChatClient ChatClient { get; }

    public IReadOnlyList<ChatMessage> Messages
    {
        get
        {
            lock (this.syncLock)
            {
                return this.messages.AsReadOnly();
            }
        }
    }

    public static AgentSession Create(
        IChatClient chatClient,
        IToolRegistry? toolRegistry = null)
    {
        return new AgentSession(chatClient, toolRegistry);
    }

    public async IAsyncEnumerable<AgentSessionUpdate> Process(
        IAsyncEnumerable<SessionInputEvent> inputEvents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputEvents);

        var inputEnumerator = inputEvents.GetAsyncEnumerator(cancellationToken);
        var pendingInputs = new Queue<SessionInputEvent>();
        IAsyncEnumerator<ChatResponseUpdate>? providerEnumerator = null;
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
                        this.AppendMessages(nextInput.Messages);
                        yield return new AgentSessionUpdate
                        {
                            Messages = this.Messages,
                            ResponseUpdate = null,
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
                        var update = providerEnumerator.Current;
                        this.AppendResponseUpdate(update);
                        yield return new AgentSessionUpdate
                        {
                            Messages = this.Messages,
                            ResponseUpdate = update,
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
                    var update = providerEnumerator.Current;
                    this.AppendResponseUpdate(update);
                    yield return new AgentSessionUpdate
                    {
                        Messages = this.Messages,
                        ResponseUpdate = update,
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

    private (IAsyncEnumerator<ChatResponseUpdate> Enumerator, CancellationTokenSource Cts) StartProvider(
        CancellationToken cancellationToken)
    {
        var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var currentMessages = this.GetCurrentMessages();
        var providerEnumerator = this.ChatClient
            .GetStreamingResponseAsync(currentMessages, cancellationToken: providerCts.Token)
            .GetAsyncEnumerator(providerCts.Token);
        return (providerEnumerator, providerCts);
    }

    private IReadOnlyList<ChatMessage> GetCurrentMessages()
    {
        lock (this.syncLock)
        {
            return this.messages.AsReadOnly();
        }
    }

    private void AppendMessages(
        IEnumerable<ChatMessage> messages)
    {
        lock (this.syncLock)
        {
            this.messages.AddRange(messages);
        }
    }

    private void AppendResponseUpdate(
        ChatResponseUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Text))
        {
            return;
        }

        lock (this.syncLock)
        {
            var lastMessage = this.messages.FirstOrDefault(m => m.Role == ChatRole.Assistant);
            if (lastMessage is not null && lastMessage.Text is not null)
            {
                var index = this.messages.IndexOf(lastMessage);
                this.messages[index] = new ChatMessage(ChatRole.Assistant, lastMessage.Text + update.Text);
            }
            else
            {
                this.messages.Add(new ChatMessage(ChatRole.Assistant, update.Text));
            }
        }
    }
}


