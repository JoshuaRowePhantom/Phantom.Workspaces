using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
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

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task AgentChat_Dispose_WithFrozenDomainTime_CancelsWhenDisposalTimeoutExpires()
    {
        var client = new DeterministicTestChatClient();
        var frozenDomainTime = new FakeTimeProvider();
        var disposalTime = new FakeTimeProvider();
        var agent = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);
        var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agent,
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            TimeProvider = frozenDomainTime,
            DisposalTimeProvider = disposalTime,
        });

        chat.EnqueueUserMessage("hello");
        await client.WaitForRequestAsync();

        var disposal = chat.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        disposalTime.Advance(AgentChat.DisposeDrainTimeout);

        await disposal;
    }

    [Fact]
    public void InternalCreateAgentChatRequest_DisposalTimeProvider_DefaultsToSystemTime()
    {
        var request = new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
        };

        Assert.Same(TimeProvider.System, request.DisposalTimeProvider);
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

    [Fact]
    public async Task DisposeAsync_OwnedResourceFailure_DisposesRemainingProductionResourcesAndReportsFailure()
    {
        var first = new ThrowingResource();
        var second = new TrackingResource();
        var childResource = new TrackingResource();
        var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            OwnedResources = [first, second],
        });
        await chat.GetOrCreateAsync(
            "child",
            AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson),
            "tool-call");
        var child = Assert.IsType<AgentChat>(Assert.Single(chat.SubAgents));
        child.RegisterOwnedResource(childResource);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => chat.DisposeAsync().AsTask());

        Assert.True(first.DisposeAttempted);
        Assert.True(second.Disposed);
        Assert.True(childResource.Disposed);
        Assert.Contains(failure.InnerExceptions,
            error => error.Message == "owned resource failed");
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

    private sealed class ThrowingResource : IAsyncDisposable
    {
        internal bool DisposeAttempted { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeAttempted = true;
            return ValueTask.FromException(new InvalidOperationException("owned resource failed"));
        }
    }

    private sealed class TrackingResource : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
