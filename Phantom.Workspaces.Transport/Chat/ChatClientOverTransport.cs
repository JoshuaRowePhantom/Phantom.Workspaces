using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Transport.Chat;

public sealed class ChatClientOverTransport : IChatClient
{
    private readonly ITransport transport;
    private readonly JsonElement request;
    private readonly SdkSessionSink sdkSessionSink;
    private readonly SemaphoreSlim openLock = new(1, 1);
    private IMessageChannel? channel;
    private bool disposed;
    private string? pendingResumeSessionId;
    private bool pendingResumeSet;

    public ChatClientOverTransport(ITransport transport, JsonElement chatClientRequest)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.request = chatClientRequest.Clone();
        this.sdkSessionSink = new SdkSessionSink(this);
    }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.channel is not null)
        {
            return;
        }

        await this.openLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.channel is not null)
            {
                return;
            }

            this.channel = await this.transport
                .ConnectToMessageChannelAsync(this.BuildOpenRequest(), ct)
                .ConfigureAwait(false);
            await this.FlushPendingResumeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.openLock.Release();
        }
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = options;
        await this.OpenAsync(cancellationToken).ConfigureAwait(false);
        var frame = new
        {
            type = "process-streaming",
            messages = ChatClientTransportListener.ToJsonElement(messages.ToArray()),
        };
        await this.channel!.Writer.WriteAsync(ChatClientTransportListener.ToJsonElement(frame), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var inbound = await this.channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var type = inbound.GetProperty("type").GetString();
            if (string.Equals(type, "streaming-update-complete", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            if (string.Equals(type, "streaming-error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(inbound.GetProperty("error").GetString());
            }

            if (string.Equals(type, CopilotSdkTransportFrames.SessionEstablishedType, StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = inbound.TryGetProperty(CopilotSdkTransportFrames.SessionIdProperty, out var sidElement)
                    ? sidElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    this.sdkSessionSink.RaiseSessionEstablished(sessionId);
                }

                continue;
            }

            if (string.Equals(type, "streaming-update", StringComparison.OrdinalIgnoreCase))
            {
                var update = ChatClientTransportListener.FromJsonElement<ChatResponseUpdate>(inbound.GetProperty("content"));
                if (update is not null)
                {
                    yield return update;
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ICopilotSdkSessionSink))
        {
            return this.sdkSessionSink;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        // IChatClient only exposes synchronous Dispose(); blocking here is the only way to ensure
        // the async message-channel close is sent before callers treat the adapter as disposed.
        // Channel disposal uses ConfigureAwait(false) or completes synchronously, so this cannot
        // deadlock on a captured synchronization context.
        this.channel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.channel = null;
    }

    private async Task FlushPendingResumeAsync(CancellationToken ct)
    {
        string? id;
        bool shouldSend;
        lock (this.sdkSessionSink)
        {
            id = this.pendingResumeSessionId;
            shouldSend = this.pendingResumeSet;
            this.pendingResumeSet = false;
        }

        if (shouldSend && this.channel is not null)
        {
            await this.channel.Writer
                .WriteAsync(CopilotSdkTransportFrames.BuildSetResumeSessionId(id), ct)
                .ConfigureAwait(false);
        }
    }

    // Clones the constructor-supplied chat-client request and injects the currently buffered
    // resume session id (if any) as a top-level property so ChatClientTransportListener can arm
    // the remote CopilotSdkChatClient synchronously before the frame pump starts. The buffered id
    // is NOT cleared here — FlushPendingResumeAsync still sends the frame-form redundantly, which
    // is idempotent and keeps the post-open SetResumeSessionId path working for later reconnects.
    private JsonElement BuildOpenRequest()
    {
        string? id;
        bool has;
        lock (this.sdkSessionSink)
        {
            id = this.pendingResumeSessionId;
            has = this.pendingResumeSet && !string.IsNullOrWhiteSpace(id);
        }

        if (!has || this.request.ValueKind != JsonValueKind.Object)
        {
            return this.request;
        }

        var node = JsonNode.Parse(this.request.GetRawText()) as JsonObject;
        if (node is null)
        {
            return this.request;
        }

        node[CopilotSdkTransportFrames.ResumeSessionIdInitialProperty] = id;
        return JsonSerializer.SerializeToElement(node);
    }

    private void SetResumeSessionId(string? sessionId)
    {
        string? normalized = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        bool sendNow;
        lock (this.sdkSessionSink)
        {
            this.pendingResumeSessionId = normalized;
            this.pendingResumeSet = true;
            sendNow = this.channel is not null;
        }

        if (sendNow)
        {
            // Fire-and-forget: the channel writer is thread-safe, and losing this frame would only
            // occur alongside a transport error that also fails the next process-streaming write
            // (which will bubble up on the streaming call). The set frame carries the same id as
            // the source's next OpenAsync will replay, so tests observe deterministic delivery.
            _ = this.FlushPendingResumeAsync(CancellationToken.None);
        }
    }

    private sealed class SdkSessionSink : ICopilotSdkSessionSink
    {
        private readonly ChatClientOverTransport owner;

        public SdkSessionSink(ChatClientOverTransport owner)
        {
            this.owner = owner;
        }

        public event Action<string>? SessionEstablished;

        public void SetResumeSessionId(string? sessionId) => this.owner.SetResumeSessionId(sessionId);

        internal void RaiseSessionEstablished(string sessionId) => this.SessionEstablished?.Invoke(sessionId);
    }
}
