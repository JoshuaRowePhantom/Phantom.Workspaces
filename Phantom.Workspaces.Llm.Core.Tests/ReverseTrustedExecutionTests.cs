using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ReverseTrustedExecutionTests
{
    private sealed class StreamingConnection : IReverseConnection
    {
        private readonly string[] chunks;

        public StreamingConnection(string clientInstanceId, params string[] chunks)
        {
            this.ClientInstanceId = clientInstanceId;
            this.chunks = chunks;
        }

        public string ClientInstanceId { get; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UnixEpoch;
        public int InFlightCount => 0;
        public RemoteAgentRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            foreach (var chunk in this.chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }
    }

    [Fact]
    public async Task ReverseRemoteChatClient_StreamsUpdatesFromConnection()
    {
        var registry = new ReverseExecutionRegistry();
        var connection = new StreamingConnection("computer-a", "Hello, ", "world");
        registry.Register(connection);

        var client = new ReverseRemoteChatClient(registry, "computer-a", "{\"definition\":{}}", agentSessionId: "session-1");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("session-1", connection.LastRequest!.AgentSessionId);
        Assert.Equal("hi", connection.LastRequest!.Messages.Single().Text);
    }

    [Fact]
    public async Task ReverseRemoteChatClient_GetResponse_AggregatesStreamedText()
    {
        var registry = new ReverseExecutionRegistry();
        registry.Register(new StreamingConnection("computer-a", "Hello, ", "world"));

        var client = new ReverseRemoteChatClient(registry, "computer-a", "{\"definition\":{}}");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Contains("Hello, world", response.Text);
    }

    [Fact]
    public async Task ReverseRemoteChatClient_Throws_WhenInstanceNotConnected()
    {
        var registry = new ReverseExecutionRegistry();
        var client = new ReverseRemoteChatClient(registry, "computer-a", "{\"definition\":{}}");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });
    }

    [Fact]
    public void ReverseTrustedExecutor_CanExecute_ReflectsRegistryAndRejectsLocal()
    {
        var registry = new ReverseExecutionRegistry();
        registry.Register(new StreamingConnection("computer-a"));
        var executor = new ReverseTrustedExecutor(registry);

        Assert.True(executor.CanExecute("computer-a"));
        Assert.False(executor.CanExecute("computer-b"));
        Assert.False(executor.CanExecute(TrustProfile.LocalClientInstance));
    }

    [Fact]
    public async Task ReverseTrustedExecutor_CreateAgentChat_Throws_WhenNotConnected()
    {
        var registry = new ReverseExecutionRegistry();
        var executor = new ReverseTrustedExecutor(registry);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.CreateAgentChatAsync(new TrustedExecutionRequest
        {
            AgentDefinition = AgentSchema.AgentDefinition.FromJson("""{ "kind": "prompt", "name": "x" }"""),
            TrustProfile = new TrustProfile { HostingWorkspacesClientInstances = ["computer-a"] },
            TargetClientInstance = "computer-a",
        }));
    }
}
