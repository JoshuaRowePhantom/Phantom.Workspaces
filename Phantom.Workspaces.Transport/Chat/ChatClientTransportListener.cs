using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Transport.Chat;

public sealed class ChatClientTransportListener : ITransportListener
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IChatClient? chatClient;
    private readonly Func<AgentDefinition, CancellationToken, Task<IChatClient>>? chatClientBuilder;

    public ChatClientTransportListener(IChatClient chatClient)
    {
        this.chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        this.chatClientBuilder = null;
    }

    public ChatClientTransportListener(Func<AgentDefinition, CancellationToken, Task<IChatClient>> chatClientBuilder)
    {
        this.chatClientBuilder = chatClientBuilder ?? throw new ArgumentNullException(nameof(chatClientBuilder));
        this.chatClient = null;
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsType(request, "chat-client"))
        {
            return null;
        }

        // If builder is supplied and request has agent-definition, build per-channel client
        if (this.chatClientBuilder is not null
            && request.TryGetProperty("agent-definition", out var agentDefElement)
            && agentDefElement.ValueKind == JsonValueKind.String)
        {
            var agentDefJson = agentDefElement.GetString();
            if (agentDefJson is null)
            {
                throw new InvalidOperationException("Agent definition property is present but null.");
            }

            var definition = AgentDefinition.FromJson(agentDefJson);
            if (definition is null)
            {
                throw new InvalidOperationException("Failed to parse agent definition from JSON.");
            }

            var client = await this.chatClientBuilder(definition, ct).ConfigureAwait(false);
            var session = new ChatClientTransportSession(client, channel, ct);
            return new PerChannelClientLifetime(session, client);
        }

        // Legacy path: use pre-built client
        if (this.chatClient is null)
        {
            throw new InvalidOperationException("No pre-built chat client available and agent-definition was not provided.");
        }

        return new ChatClientTransportSession(this.chatClient, channel, ct);
    }

    public ValueTask DisposeAsync()
    {
        this.chatClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static JsonElement ToJsonElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    internal static T? FromJsonElement<T>(JsonElement value) => value.Deserialize<T>(JsonOptions);

    private static bool IsType(JsonElement request, string type)
        => request.ValueKind == JsonValueKind.Object
           && request.TryGetProperty("type", out var typeElement)
           && string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase);

    private sealed class PerChannelClientLifetime : IAsyncDisposable
    {
        private readonly ChatClientTransportSession session;
        private readonly IChatClient client;

        public PerChannelClientLifetime(ChatClientTransportSession session, IChatClient client)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async ValueTask DisposeAsync()
        {
            await this.session.DisposeAsync().ConfigureAwait(false);
            this.client.Dispose();
        }
    }
}
