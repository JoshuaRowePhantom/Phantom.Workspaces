using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Chat;

public sealed class ChatClientTransportListenerTests
{
    [Fact]
    public async Task ChatClientTransportListener_ReceivesAgentDefinition_BuildsChatClientViaAgentFactory()
    {
        // Arrange: Create a spy builder that records the AgentDefinition and returns a test client
        AgentDefinition? capturedDefinition = null;
        var testClient = new SpyChatClient();
        
        async Task<IChatClient> BuilderSpy(AgentDefinition definition, CancellationToken ct)
        {
            await Task.Yield();
            capturedDefinition = definition;
            return testClient;
        }

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(BuilderSpy));
        await using var transport = new LocalTransport(registry);

        var agentDef = new PromptAgent
        {
            Name = "test-agent",
            Instructions = "test instructions",
            Model = new Model { Provider = "echo", Id = "echo-model" },
        };
        var agentDefJson = agentDef.ToJson();

        // Note: Use kebab-case property name to match TransportTrustedExecutor
        var openRequest = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            ["type"] = "chat-client",
            ["agent-definition"] = agentDefJson
        }).RootElement.Clone();

        // Act: Open channel with agent-definition
        var channel = await transport.ConnectToMessageChannelAsync(openRequest, TestCancellationToken());

        // Send a chat message
        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"hello"}}"""), TestCancellationToken());
        
        // Read response to ensure the built client is being used
        var update = await channel.Reader.ReadAsync(TestCancellationToken());

        // Assert: Builder was invoked with the correct definition
        Assert.NotNull(capturedDefinition);
        Assert.Equal("test-agent", (capturedDefinition as PromptAgent)?.Name);
        Assert.Equal("echo", (capturedDefinition as PromptAgent)?.Model?.Provider);
        
        // Assert: The built client received the request
        Assert.True(testClient.WasInvoked);
        Assert.Equal("streaming-update", update.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatClientTransportListener_PerChannelClientLifetime_DisposesOnChannelClose()
    {
        // This test verifies that the PerChannelClientLifetime wrapper correctly disposes
        // the built client. We can't easily test this through LocalTransport (which doesn't
        // track session leases), so we verify it directly by calling the listener's
        // OnChannelOpenAsync and disposing the returned lease.
        
        var disposableSpy = new DisposableSpyChatClient();
        
        async Task<IChatClient> Builder(AgentDefinition definition, CancellationToken ct)
        {
            await Task.Yield();
            return disposableSpy;
        }

        var listener = new ChatClientTransportListener(Builder);

        var agentDef = new PromptAgent
        {
            Name = "test-agent",
            Model = new Model { Provider = "echo", Id = "echo-model" },
        };

        var openRequest = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            ["type"] = "chat-client",
            ["agent-definition"] = agentDef.ToJson()
        }).RootElement.Clone();

        // Create a mock channel
        var mockChannel = new MockMessageChannel();

        // Act: Call OnChannelOpenAsync to get the session lease
        var lease = await listener.OnChannelOpenAsync(openRequest, mockChannel, CancellationToken.None);
        Assert.NotNull(lease);
        Assert.False(disposableSpy.IsDisposed, "Client should not be disposed before lease disposal");
        
        // Dispose the lease (simulates what ReverseExecutionDispatcher does)
        await lease.DisposeAsync();

        // Assert: The built client was disposed
        Assert.True(disposableSpy.IsDisposed, "Client should be disposed after lease disposal");
    }

    [Fact]
    public async Task ChatClientTransportListener_LegacyPreBuiltChatClientPath_StillWorks()
    {
        // Arrange: Use the legacy constructor with a pre-built client
        var preBuiltClient = new SpyChatClient();
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(preBuiltClient));
        await using var transport = new LocalTransport(registry);

        // Act: Open channel WITHOUT agent-definition (legacy payload)
        var channel = await transport.ConnectToMessageChannelAsync(
            Json("""{"type":"chat-client"}"""), 
            TestCancellationToken());

        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"hello"}}"""), TestCancellationToken());
        var update = await channel.Reader.ReadAsync(TestCancellationToken());

        // Assert: The pre-built client was used
        Assert.True(preBuiltClient.WasInvoked);
        Assert.Equal("streaming-update", update.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReverseExecutionDispatcher_RegistersChatClientListener_ServesChatClientChannels()
    {
        // Arrange: Create a harness with a builder-based listener
        var testClient = new SpyChatClient();
        
        async Task<IChatClient> Builder(AgentDefinition definition, CancellationToken ct)
        {
            await Task.Yield();
            return testClient;
        }

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(Builder));
        
        await using var harness = await HubRelayHarness.CreateAsync(registry, TestCancellationToken());

        var agentDef = new PromptAgent
        {
            Name = "dispatcher-test",
            Model = new Model { Provider = "echo", Id = "echo-model" },
        };

        // Note: Use kebab-case property name to match TransportTrustedExecutor
        var openRequest = JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            ["type"] = "chat-client",
            ["agent-definition"] = agentDef.ToJson()
        }).RootElement.Clone();

        // Act: Connect through the harness (simulates reverse-http dispatch)
        await using var forwardingTransport = await harness.ConnectMachineBAsync(TestCancellationToken());

        var channel = await forwardingTransport.ConnectToMessageChannelAsync(openRequest, TestCancellationToken());
        
        await channel.Writer.WriteAsync(Json("""{"type":"process-streaming","content":{"role":"user","text":"test"}}"""), TestCancellationToken());
        var update = await channel.Reader.ReadAsync(TestCancellationToken());

        // Assert: The listener was invoked through the dispatcher
        Assert.True(testClient.WasInvoked);
        Assert.Equal("streaming-update", update.GetProperty("type").GetString());
    }

    private static CancellationToken TestCancellationToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class SpyChatClient : IChatClient
    {
        public bool WasInvoked { get; private set; }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) 
            => throw new NotSupportedException();
        
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, 
            ChatOptions? options = null, 
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.WasInvoked = true;
            _ = messages.ToArray();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "spy response");
        }
    }

    private sealed class DisposableSpyChatClient : IChatClient
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => this.IsDisposed = true;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) 
            => throw new NotSupportedException();
        
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, 
            ChatOptions? options = null, 
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages.ToArray();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "disposable response");
        }
    }

    private sealed class MockMessageChannel : IMessageChannel
    {
        private readonly System.Threading.Channels.Channel<JsonElement> channel = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();

        public System.Threading.Channels.ChannelWriter<JsonElement> Writer => this.channel.Writer;
        public System.Threading.Channels.ChannelReader<JsonElement> Reader => this.channel.Reader;

        public ValueTask DisposeAsync()
        {
            this.channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
