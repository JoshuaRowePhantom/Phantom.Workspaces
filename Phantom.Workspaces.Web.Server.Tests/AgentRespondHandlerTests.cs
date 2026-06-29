using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class AgentRespondHandlerTests
{
    private const string EchoAgentJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private sealed class StubReverseHandler : IReverseExecutionHandler
    {
        private readonly string[] chunks;

        public StubReverseHandler(params string[] chunks) => this.chunks = chunks;

        public RemoteAgentRequest? Received { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            this.Received = request;
            foreach (var chunk in this.chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public Task HandleStreamAsync(
            string streamKind,
            string openPayloadJson,
            Phantom.Workspaces.Llm.Shell.IStreamMessageChannel channel,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task RespondAsync_EchoAgent_ReturnsEchoedText()
    {
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            Messages = [new ChatMessage(ChatRole.User, "hello-remote")],
        };

        var response = await AgentRespondHandler.RespondAsync(request);

        Assert.Equal("hello-remote", response.Text);
    }

    [Fact]
    public async Task RespondAsync_WhenTargetInstanceConnected_RelaysToReversePeer()
    {
        var pair = new InMemoryReverseMessageChannelPair();
        var registry = new ReverseExecutionRegistry();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ConnectionsChanged += (_, _) =>
        {
            if (registry.IsConnected("computer-a"))
            {
                connected.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource();
        var acceptor = new ReverseConnectionAcceptor(registry);
        _ = acceptor.AcceptAsync(pair.ServerEnd, cts.Token);

        var handler = new StubReverseHandler("Hello, ", "peer");
        var worker = new ReverseExecutionWorker(pair.ClientEnd, "computer-a", handler);
        _ = worker.RunAsync(cts.Token);

        await connected.Task;

        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            TargetClientInstance = "computer-a",
            Messages = [new ChatMessage(ChatRole.User, "hi-peer")],
        };

        var response = await AgentRespondHandler.RespondAsync(request, registry, cts.Token);

        Assert.Equal("hi-peer", handler.Received!.Messages.Single().Text);
        Assert.Contains("Hello, peer", response.Text);
        cts.Cancel();
    }

    [Fact]
    public async Task RespondAsync_WhenTargetInstanceNotConnected_RunsLocally()
    {
        var registry = new ReverseExecutionRegistry();
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            TargetClientInstance = "not-connected",
            Messages = [new ChatMessage(ChatRole.User, "hello-local")],
        };

        var response = await AgentRespondHandler.RespondAsync(request, registry);

        Assert.Equal("hello-local", response.Text);
    }

    [Fact]
    public void RemoteAgentRequest_RoundTrips_WithAiJsonOptions()
    {
        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = EchoAgentJson,
            AgentSessionId = "session-1",
            TargetClientInstance = "computer-a",
            Messages = [new ChatMessage(ChatRole.User, "ping")],
        };

        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        var roundTripped = JsonSerializer.Deserialize<RemoteAgentRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal("session-1", roundTripped!.AgentSessionId);
        Assert.Equal("computer-a", roundTripped.TargetClientInstance);
        Assert.Single(roundTripped.Messages);
        Assert.Equal("ping", roundTripped.Messages[0].Text);
    }
}
