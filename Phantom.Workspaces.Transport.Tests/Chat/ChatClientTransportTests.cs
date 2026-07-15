using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Transport.Tests.Chat;

public sealed class ChatClientTransportTests
{
    [Fact]
    public async Task ChatClientTransportListener_ProcessStreaming_EmitsUpdates()
    {
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(new EchoChatClient()));
        await using var transport = new LocalTransport(registry);
        var channel = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), TestCancellationToken());

        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"hello"}}"""), TestCancellationToken());
        var update = await channel.Reader.ReadAsync(TestCancellationToken());
        var complete = await channel.Reader.ReadAsync(TestCancellationToken());

        Assert.Equal("streaming-update", update.GetProperty("type").GetString());
        Assert.Equal("streaming-update-complete", complete.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatClientTransportListener_StreamingError_EmitsErrorFrame()
    {
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(new ThrowingChatClient()));
        await using var transport = new LocalTransport(registry);
        var channel = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), TestCancellationToken());

        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"hello"}}"""), TestCancellationToken());
        var error = await channel.Reader.ReadAsync(TestCancellationToken());

        Assert.Equal("streaming-error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatClientOverTransport_GetStreamingResponse_RoundTrips()
    {
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(new EchoChatClient()));
        await using var transport = new LocalTransport(registry);
        using var client = new ChatClientOverTransport(transport, Json("""{"type":"chat-client"}"""));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], null, TestCancellationToken()))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
    }

    private static CancellationToken TestCancellationToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class EchoChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "pong");
            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (DateTime.MinValue == DateTime.MaxValue)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "never");
            }

            throw new InvalidOperationException("boom");
        }
    }
}

