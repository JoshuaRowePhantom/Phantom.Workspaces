using Microsoft.Extensions.AI;
using System.Collections.Concurrent;

namespace Phantom.Workspaces.Llm;

public sealed class DeterministicTestChatClient : IChatClient
{
    private readonly ConcurrentQueue<QueuedResponse> responseQueue = new();
    private readonly SemaphoreSlim responseSignal = new(0);
    private readonly ConcurrentQueue<QueuedStreamResponse> streamQueue = new();
    private readonly SemaphoreSlim streamSignal = new(0);
    private readonly TaskCompletionSource requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeterministicTestChatClient()
    {
    }

    public IReadOnlyList<ChatMessage> LastRequestMessages { get; private set; } = [];

    public Task WaitForRequestAsync(CancellationToken cancellationToken = default)
        => this.requestReceived.Task.WaitAsync(cancellationToken);

    public QueuedResponse EnqueueResponse(ChatResponse response, bool isReady = true)
    {
        var queued = new QueuedResponse(response, null, isReady);
        this.responseQueue.Enqueue(queued);
        this.responseSignal.Release();
        return queued;
    }

    public QueuedResponse EnqueueResponseException(Exception exception, bool isReady = true)
    {
        var queued = new QueuedResponse(null, exception, isReady);
        this.responseQueue.Enqueue(queued);
        this.responseSignal.Release();
        return queued;
    }

    public QueuedStreamResponse EnqueueStreamingResponse(bool isReady = true)
    {
        var queued = new QueuedStreamResponse(isReady);
        this.streamQueue.Enqueue(queued);
        this.streamSignal.Release();
        return queued;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        this.LastRequestMessages = messages.ToArray();
        this.requestReceived.TrySetResult();

        await this.responseSignal.WaitAsync(cancellationToken);
        if (!this.responseQueue.TryDequeue(out var queuedResponse))
        {
            throw new InvalidOperationException("No queued non-streaming response was available.");
        }

        await queuedResponse.WaitUntilReadyAsync(cancellationToken);
        if (queuedResponse.Exception is not null)
        {
            throw queuedResponse.Exception;
        }

        if (queuedResponse.Response is not null)
        {
            return queuedResponse.Response;
        }

        throw new InvalidOperationException("Queued non-streaming response had no response payload.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        this.LastRequestMessages = messages.ToArray();
        this.requestReceived.TrySetResult();

        await this.streamSignal.WaitAsync(cancellationToken);
        if (!this.streamQueue.TryDequeue(out var queuedStream))
        {
            throw new InvalidOperationException("No queued streaming response was available.");
        }

        await queuedStream.WaitUntilReadyAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = await queuedStream.DequeueAsync(cancellationToken);
            await item.WaitUntilReadyAsync(cancellationToken);

            if (item.IsTerminal)
            {
                if (item.Exception is not null)
                {
                    throw item.Exception;
                }

                yield break;
            }

            if (item.Update is null)
            {
                throw new InvalidOperationException("Queued streaming update item did not include an update payload.");
            }

            yield return item.Update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
    }

    public sealed class QueuedResponse
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal QueuedResponse(ChatResponse? response, Exception? exception, bool isReady)
        {
            this.Response = response;
            this.Exception = exception;
            if (isReady)
            {
                this.MarkReady();
            }
        }

        internal ChatResponse? Response { get; }

        internal Exception? Exception { get; }

        public void MarkReady() => this.ready.TrySetResult();

        internal Task WaitUntilReadyAsync(CancellationToken cancellationToken)
            => this.ready.Task.WaitAsync(cancellationToken);
    }

    public sealed class QueuedStreamResponse
    {
        private readonly ConcurrentQueue<QueuedStreamItem> items = new();
        private readonly SemaphoreSlim itemSignal = new(0);
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal QueuedStreamResponse(bool isReady)
        {
            if (isReady)
            {
                this.MarkReady();
            }
        }

        public void MarkReady() => this.ready.TrySetResult();

        public QueuedStreamItem EnqueueUpdate(ChatResponseUpdate update, bool isReady = true)
        {
            var item = QueuedStreamItem.ForUpdate(update, isReady);
            this.items.Enqueue(item);
            this.itemSignal.Release();
            return item;
        }

        public QueuedStreamItem EnqueueException(Exception exception, bool isReady = true)
        {
            var item = QueuedStreamItem.ForTerminal(exception, isReady);
            this.items.Enqueue(item);
            this.itemSignal.Release();
            return item;
        }

        public QueuedStreamItem Complete(bool isReady = true)
        {
            var item = QueuedStreamItem.ForTerminal(exception: null, isReady);
            this.items.Enqueue(item);
            this.itemSignal.Release();
            return item;
        }

        internal Task WaitUntilReadyAsync(CancellationToken cancellationToken)
            => this.ready.Task.WaitAsync(cancellationToken);

        internal async Task<QueuedStreamItem> DequeueAsync(CancellationToken cancellationToken)
        {
            await this.itemSignal.WaitAsync(cancellationToken);
            if (this.items.TryDequeue(out var item))
            {
                return item;
            }

            throw new InvalidOperationException("No queued streaming item was available.");
        }

    }

    public sealed class QueuedStreamItem
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private QueuedStreamItem(ChatResponseUpdate? update, Exception? exception, bool isTerminal, bool isReady)
        {
            this.Update = update;
            this.Exception = exception;
            this.IsTerminal = isTerminal;
            if (isReady)
            {
                this.MarkReady();
            }
        }

        internal ChatResponseUpdate? Update { get; }

        internal Exception? Exception { get; }

        internal bool IsTerminal { get; }

        public void MarkReady() => this.ready.TrySetResult();

        internal Task WaitUntilReadyAsync(CancellationToken cancellationToken)
            => this.ready.Task.WaitAsync(cancellationToken);

        internal static QueuedStreamItem ForUpdate(ChatResponseUpdate update, bool isReady)
            => new(update, exception: null, isTerminal: false, isReady);

        internal static QueuedStreamItem ForTerminal(Exception? exception, bool isReady)
            => new(update: null, exception, isTerminal: true, isReady);
    }
}
