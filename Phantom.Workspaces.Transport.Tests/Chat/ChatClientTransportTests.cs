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

    [Fact]
    public async Task ChatClientTransportListener_Interrupt_CancelsTurn()
    {
        var chatClient = new InterruptibleChatClient();
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(chatClient));
        await using var transport = new LocalTransport(registry);
        var channel = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), TestCancellationToken());

        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"hello"}}"""), TestCancellationToken());

        // Reading the first update guarantees the turn is in progress and awaiting cancellation.
        var first = await channel.Reader.ReadAsync(TestCancellationToken());
        Assert.Equal("streaming-update", first.GetProperty("type").GetString());

        await channel.Writer.WriteAsync(Json("""{"type":"interrupt"}"""), TestCancellationToken());

        var complete = await channel.Reader.ReadAsync(TestCancellationToken());
        Assert.Equal("streaming-update-complete", complete.GetProperty("type").GetString());
        Assert.True(await chatClient.Cancelled.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ChatClientTransportListener_Steering_InjectsMessage()
    {
        var chatClient = new SteerableChatClient();
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(chatClient));
        await using var transport = new LocalTransport(registry);
        var channel = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), TestCancellationToken());

        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"start"}}"""), TestCancellationToken());
        await channel.Writer.WriteAsync(SteeringFrame("steer now"), TestCancellationToken());

        var update = await channel.Reader.ReadAsync(TestCancellationToken());
        var complete = await channel.Reader.ReadAsync(TestCancellationToken());

        Assert.Equal("streaming-update", update.GetProperty("type").GetString());
        var injected = await chatClient.Injected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("steer now", injected);
        Assert.Contains("steer now", update.GetProperty("content").GetRawText());
        Assert.Equal("streaming-update-complete", complete.GetProperty("type").GetString());
    }

    private static CancellationToken TestCancellationToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Serializes a real ChatMessage into a steering frame so ChatMessage.Contents (and therefore
    // ChatMessage.Text) round-trips; the "text" JSON shorthand does not populate Contents.
    private static JsonElement SteeringFrame(string text)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.SerializeToElement(
            new { type = "steering", content = new ChatMessage(ChatRole.User, text) },
            options);
    }

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

    private sealed class InterruptibleChatClient : IChatClient
    {
        private readonly TaskCompletionSource<bool> cancelledTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Cancelled => this.cancelledTcs.Task;

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages.ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "started");

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => waiter.TrySetResult(true)))
            {
                await waiter.Task.ConfigureAwait(false);
            }

            this.cancelledTcs.TrySetResult(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class SteerableChatClient : IChatClient, IChatSteeringTarget
    {
        private readonly TaskCompletionSource<string> injectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Injected => this.injectedTcs.Task;

        public void InjectSteeringMessage(ChatMessage message) => this.injectedTcs.TrySetResult(message.Text ?? string.Empty);

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatSteeringTarget) ? this : null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages.ToArray();
            var steered = await this.injectedTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, steered);
        }
    }
}

