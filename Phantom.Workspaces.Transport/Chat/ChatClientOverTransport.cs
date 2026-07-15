using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Transport.Chat;

public sealed class ChatClientOverTransport : IChatClient
{
    private readonly ITransport transport;
    private readonly JsonElement request;
    private IMessageChannel? channel;
    private bool disposed;

    public ChatClientOverTransport(ITransport transport, JsonElement chatClientRequest)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.request = chatClientRequest.Clone();
    }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.channel is not null)
        {
            return;
        }

        this.channel = await this.transport.ConnectToMessageChannelAsync(this.request, ct).ConfigureAwait(false);
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
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.channel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.channel = null;
    }
}
