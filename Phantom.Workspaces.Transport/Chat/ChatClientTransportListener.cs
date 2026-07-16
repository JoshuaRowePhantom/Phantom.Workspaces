using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Transport.Chat;

public sealed class ChatClientTransportListener : ITransportListener
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IChatClient chatClient;

    public ChatClientTransportListener(IChatClient chatClient)
    {
        this.chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsType(request, "chat-client"))
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        return Task.FromResult<IAsyncDisposable?>(new ChatClientTransportSession(this.chatClient, channel, ct));
    }

    public ValueTask DisposeAsync()
    {
        this.chatClient.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static JsonElement ToJsonElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    internal static T? FromJsonElement<T>(JsonElement value) => value.Deserialize<T>(JsonOptions);

    private static bool IsType(JsonElement request, string type)
        => request.ValueKind == JsonValueKind.Object
           && request.TryGetProperty("type", out var typeElement)
           && string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase);
}
