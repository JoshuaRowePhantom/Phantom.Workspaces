using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Transport.Chat;

public sealed class ChatClientTransportSession : IAsyncDisposable
{
    private readonly IChatClient chatClient;
    private readonly IMessageChannel channel;
    private readonly CancellationTokenSource sessionCts;
    private readonly Task pumpTask;
    private CancellationTokenSource? turnCts;
    private int disposed;

    internal ChatClientTransportSession(IChatClient chatClient, IMessageChannel channel, CancellationToken cancellationToken)
    {
        this.chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.pumpTask = Task.Run(() => this.RunAsync(this.sessionCts.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.sessionCts.CancelAsync().ConfigureAwait(false);
        if (this.turnCts is not null)
        {
            await this.turnCts.CancelAsync().ConfigureAwait(false);
            this.turnCts.Dispose();
        }

        await SuppressAsync(this.pumpTask).ConfigureAwait(false);
        await this.channel.DisposeAsync().ConfigureAwait(false);
        this.sessionCts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in this.channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!frame.TryGetProperty("type", out var typeElement))
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "process-streaming", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(() => this.ProcessStreamingAsync(frame.Clone(), ct), CancellationToken.None);
                }
                else if (string.Equals(type, "steering", StringComparison.OrdinalIgnoreCase))
                {
                    this.InjectSteering(frame.Clone());
                }
                else if (string.Equals(type, "interrupt", StringComparison.OrdinalIgnoreCase))
                {
                    if (this.turnCts is not null)
                    {
                        await this.turnCts.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void InjectSteering(JsonElement frame)
    {
        // Steering forwards a mid-turn message to the in-progress turn's chat client when that
        // client exposes the IChatSteeringTarget capability (mirrors CopilotSdkChatClient's
        // immediate steering). Clients without the capability silently ignore steering frames.
        if (this.chatClient.GetService(typeof(IChatSteeringTarget)) is not IChatSteeringTarget target)
        {
            return;
        }

        if (!frame.TryGetProperty("content", out var contentElement))
        {
            return;
        }

        var message = ChatClientTransportListener.FromJsonElement<ChatMessage>(contentElement);
        if (message is not null)
        {
            target.InjectSteeringMessage(message);
        }
    }

    private async Task ProcessStreamingAsync(JsonElement frame, CancellationToken sessionToken)
    {
        this.turnCts?.Dispose();
        this.turnCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        var ct = this.turnCts.Token;
        try
        {
            var messages = ReadMessages(frame);
            await foreach (var update in this.chatClient.GetStreamingResponseAsync(messages, null, ct).ConfigureAwait(false))
            {
                var outbound = new { type = "streaming-update", content = ChatClientTransportListener.ToJsonElement(update) };
                await this.channel.Writer.WriteAsync(ChatClientTransportListener.ToJsonElement(outbound), ct).ConfigureAwait(false);
            }

            await this.channel.Writer.WriteAsync(ChatClientTransportListener.ToJsonElement(new { type = "streaming-update-complete" }), sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await this.channel.Writer.WriteAsync(ChatClientTransportListener.ToJsonElement(new { type = "streaming-update-complete" }), sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await this.channel.Writer.WriteAsync(ChatClientTransportListener.ToJsonElement(new { type = "streaming-error", error = ex.Message }), sessionToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<ChatMessage> ReadMessages(JsonElement frame)
    {
        if (frame.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            return ChatClientTransportListener.FromJsonElement<ChatMessage[]>(messagesElement) ?? [];
        }

        if (frame.TryGetProperty("content", out var contentElement))
        {
            var message = ChatClientTransportListener.FromJsonElement<ChatMessage>(contentElement);
            return message is null ? [] : [message];
        }

        return [];
    }

    private static async Task SuppressAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
