using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatDisposalTests
{
    [Fact]
    public async Task AgentChat_Dispose_DoesNotHang()
    {
        // The process loop is blocked on queueStateSignal.WaitAsync waiting for input.
        // Disposal must cancel that wait so DisposeAsync returns promptly.
        var client = new DeterministicTestChatClient();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            AgentServices = new AgentServices { ChatClientOverride = client },
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await chat.DisposeAsync().AsTask().WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task AgentChat_Dispose_CancelsProcessLoop()
    {
        // The process loop is in the middle of an LLM call (waiting for a response that will
        // never arrive). Disposal must cancel the in-progress run and complete promptly.
        var client = new DeterministicTestChatClient();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            AgentServices = new AgentServices { ChatClientOverride = client },
        });

        chat.EnqueueUserMessage("hello");
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.WaitForRequestAsync(requestTimeout.Token);

        using var disposeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await chat.DisposeAsync().AsTask().WaitAsync(disposeTimeout.Token);
    }


    private const string EchoAgentJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    [Fact]
    public async Task CreateAgentChat_DisposesOwnedAsyncDisposableChatClient()
    {
        var trackingClient = new DisposalTrackingChatClient();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            AgentServices = new AgentServices { ChatClientOverride = trackingClient },
        });

        Assert.False(trackingClient.Disposed);

        await chat.DisposeAsync();

        Assert.True(trackingClient.Disposed);
    }

    private sealed class DisposalTrackingChatClient : IChatClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
