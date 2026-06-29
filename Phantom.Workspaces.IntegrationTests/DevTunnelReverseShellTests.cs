using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.IntegrationTests;

/// <summary>
/// A <see cref="FactAttribute"/> that marks the test as skipped when
/// <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> is not set in the environment.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = -1)
        : base(filePath, lineNumber)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")))
        {
            Skip = "PHANTOM_INTEGRATION_GITHUB_TOKEN is not set; skipping integration test.";
        }
    }
}

/// <summary>
/// Integration tests that exercise the end-to-end reverse-connection path through a real dev-tunnel
/// relay: an in-process Kestrel server hosts <c>/reverse/connect</c>; a real
/// <see cref="ReverseExecutionClientHost"/> connects over the relay; and execution requests are
/// sent and received over the resulting WebSocket channel.
///
/// All tests skip gracefully when <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> is not set.
/// </summary>
[Collection("DevTunnel")]
public sealed class DevTunnelReverseShellTests : IClassFixture<InProcessDevTunnelFixture>
{
    private readonly InProcessDevTunnelFixture fixture;

    public DevTunnelReverseShellTests(InProcessDevTunnelFixture fixture)
    {
        this.fixture = fixture;
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task DevTunnelReverseShell_CanConnect()
    {
        const string clientId = "integration-can-connect";
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.fixture.Registry.ConnectionsChanged += (_, _) =>
        {
            if (this.fixture.Registry.IsConnected(clientId))
            {
                connectedTcs.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handler = new StubHandler("ack");
        var client = ReverseExecutionClientHost.ForEndpoint(
            this.fixture.RelayBaseUri!.ToString(),
            clientId,
            handler,
            this.fixture.AccessToken);

        var runTask = client.RunAsync(cts.Token);

        await connectedTcs.Task.WaitAsync(cts.Token);

        Assert.True(this.fixture.Registry.IsConnected(clientId));

        cts.Cancel();
        await runTask;
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task DevTunnelReverseShell_CanOpenShellAndReceiveOutput()
    {
        const string clientId = "integration-receive-output";
        var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.fixture.Registry.ConnectionsChanged += (_, _) =>
        {
            if (this.fixture.Registry.IsConnected(clientId))
            {
                connectedTcs.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handler = new StubHandler("hello from integration");
        var client = ReverseExecutionClientHost.ForEndpoint(
            this.fixture.RelayBaseUri!.ToString(),
            clientId,
            handler,
            this.fixture.AccessToken);

        var runTask = client.RunAsync(cts.Token);

        await connectedTcs.Task.WaitAsync(cts.Token);

        Assert.True(this.fixture.Registry.TryGetConnection(clientId, out var connection));

        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = "{}",
            Messages = [new ChatMessage(ChatRole.User, "hello")],
        };

        var chunks = new List<string>();
        await foreach (var update in connection.ExecuteAsync(request, cts.Token))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        Assert.Contains("hello from integration", string.Concat(chunks));

        cts.Cancel();
        await runTask;
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task DevTunnelReverseShell_ReconnectsAfterDisconnect()
    {
        const string clientId = "integration-reconnect";
        var firstConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var handler = new StubHandler("reconnect-ack");

        // Capture the first connected socket so the test can force-abort it to trigger reconnect.
        var firstSocketTcs = new TaskCompletionSource<ClientWebSocket>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wsUri = new UriBuilder(this.fixture.RelayBaseUri!) { Scheme = "wss", Path = "/reverse/connect" }.Uri;

        async Task<IReverseMessageChannel> CreateChannelAsync(CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(wsUri, ct);
                firstSocketTcs.TrySetResult(socket);
                return new WebSocketReverseMessageChannel(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // Inject a short backoff so the reconnect happens within the test timeout.
        var client = new ReverseExecutionClientHost(
            clientId,
            handler,
            CreateChannelAsync,
            backoffDelay: (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(500), ct));

        client.ConnectionStateChanged += connected =>
        {
            if (connected)
            {
                if (!firstConnectedTcs.TrySetResult())
                {
                    // A reconnect occurred after the forced disconnect.
                    if (disconnectedTcs.Task.IsCompleted)
                    {
                        reconnectedTcs.TrySetResult();
                    }
                }
            }
            else if (firstConnectedTcs.Task.IsCompleted)
            {
                disconnectedTcs.TrySetResult();
            }
        };

        var runTask = client.RunAsync(cts.Token);

        // Wait for the first connection then abort the underlying socket.
        await firstConnectedTcs.Task.WaitAsync(cts.Token);
        Assert.True(client.IsConnected);

        var socket = await firstSocketTcs.Task.WaitAsync(cts.Token);
        socket.Abort();

        // Wait for the disconnect event, then the reconnect event.
        await disconnectedTcs.Task.WaitAsync(cts.Token);
        await reconnectedTcs.Task.WaitAsync(cts.Token);

        Assert.True(client.IsConnected);

        cts.Cancel();
        await runTask;
    }

    private sealed class StubHandler(params string[] chunks) : IReverseExecutionHandler
    {
        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
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

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
