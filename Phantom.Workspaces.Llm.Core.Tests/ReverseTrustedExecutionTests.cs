using System;
using System.Collections.Generic;
using System.IO;
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
        public string? AnnouncedEndpoint => null;
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

        public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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

    [Fact]
    public async Task ReverseTrustedExecutor_OpenStreamAsync_RelaysStreamThroughReverseChannel()
    {
        var pair = new InMemoryReverseMessageChannelPair();
        var registry = new ReverseExecutionRegistry();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ConnectionsChanged += (_, _) =>
        {
            if (registry.IsConnected("computer-a")) connected.TrySetResult();
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptor = new ReverseConnectionAcceptor(registry);
        _ = acceptor.AcceptAsync(pair.ServerEnd, cts.Token);

        // Worker handler: echoes the open payload as a Data frame, then closes.
        var workerHandler = new StreamEchoHandler();
        var worker = new ReverseExecutionWorker(pair.ClientEnd, "computer-a", workerHandler);
        _ = worker.RunAsync(cts.Token);

        await connected.Task;

        var executor = new ReverseTrustedExecutor(registry);
        var request = new TrustedStreamRequest
        {
            TargetClientInstance = "computer-a",
            StreamKind = "shell",
            OpenPayload = System.Text.Json.JsonDocument.Parse("""{"echo":"hello"}""").RootElement,
        };

        var stream = await executor.OpenStreamAsync(request, cts.Token);
        Assert.NotNull(stream);

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.True(read > 0);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(buffer, 0, read));

        await stream.DisposeAsync();
        cts.Cancel();
    }

    [Fact]
    public async Task ReverseTrustedExecutor_OpenStreamAsync_Throws_WhenNotConnected()
    {
        var registry = new ReverseExecutionRegistry();
        var executor = new ReverseTrustedExecutor(registry);
        var request = new TrustedStreamRequest
        {
            TargetClientInstance = "computer-a",
            StreamKind = "shell",
            OpenPayload = System.Text.Json.JsonDocument.Parse("{}").RootElement,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.OpenStreamAsync(request));
    }

    /// <summary>
    /// A test <see cref="IReverseExecutionHandler"/> that, on a stream open, deserialises the
    /// open payload, reads the <c>"echo"</c> field, sends its value as a Data frame, then closes.
    /// </summary>
    private sealed class StreamEchoHandler : IReverseExecutionHandler
    {
        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public async Task HandleStreamAsync(
            string streamKind,
            string openPayloadJson,
            Phantom.Workspaces.Llm.Shell.IStreamMessageChannel channel,
            CancellationToken cancellationToken)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(openPayloadJson);
            var echo = doc.RootElement.TryGetProperty("echo", out var v) ? v.GetString() ?? "" : "";
            var bytes = System.Text.Encoding.UTF8.GetBytes(echo);

            await channel.SendAsync(
                new Phantom.Workspaces.Llm.Shell.StreamFrame(
                    Phantom.Workspaces.Llm.Shell.StreamFrameKind.Data, bytes),
                cancellationToken).ConfigureAwait(false);

            await channel.DisposeAsync().ConfigureAwait(false);
        }

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
