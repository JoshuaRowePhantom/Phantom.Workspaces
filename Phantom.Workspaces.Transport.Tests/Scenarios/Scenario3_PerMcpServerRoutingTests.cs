using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Mcp;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Scenario 3 (issue #1438, per-component-executor-binding): two MCP servers bound to <b>different</b>
/// executors route their MCP channels to different machines, while a locally bound server stays
/// in-process. This exercises <see cref="ExecutorTargetRouter.ConnectToDescriptorAsync"/> — the
/// production seam a remote-bound <c>McpToolContextProvider</c> uses — feeding a resolved
/// connection-descriptor straight into the registry with no client-instance string hop. Everything is
/// hermetic: machines are in-process <see cref="TransportRegistry"/> instances reached over
/// <see cref="LocalTransport"/>, and each hosts an MCP listener that tags its replies with its machine.
/// </summary>
public sealed class Scenario3_PerMcpServerRoutingTests
{
    [Fact]
    public async Task PerMcpServer_BoundToDifferentExecutors_RouteToDifferentMachines()
    {
        var ct = TransportScenarioSupport.TestToken();

        var alphaHost = new RecordingMcpListener("A");
        var machineA = new TransportRegistry();
        machineA.Register(alphaHost);

        var betaHost = new RecordingMcpListener("B");
        var machineB = new TransportRegistry();
        machineB.Register(betaHost);

        var factoryRegistry = new TransportFactoryRegistry();
        factoryRegistry.Register(new MachineRoutingTransportFactory(new Dictionary<string, TransportRegistry>(StringComparer.Ordinal)
        {
            ["A"] = machineA,
            ["B"] = machineB,
        }));

        var router = new ExecutorTargetRouter(ExecutorTopology.SingleMachine, factoryRegistry);

        // The alpha server is bound to Machine A; its MCP channel-open must reach Machine A.
        var alphaMachine = await OpenAndReadMachineAsync(router, ProfileDescriptor("A"), McpTool("alpha", "https://alpha/mcp"), ct);
        Assert.Equal("A", alphaMachine);
        Assert.True(alphaHost.Invoked);
        Assert.False(betaHost.Invoked);

        // The beta server is bound to Machine B; its MCP channel-open must reach Machine B.
        var betaMachine = await OpenAndReadMachineAsync(router, ProfileDescriptor("B"), McpTool("beta", "https://beta/mcp"), ct);
        Assert.Equal("B", betaMachine);
        Assert.True(betaHost.Invoked);
    }

    [Fact]
    public async Task LocallyBoundMcpServer_ResolvesInProcess_NoInstanceHop()
    {
        var ct = TransportScenarioSupport.TestToken();

        var localHost = new RecordingMcpListener(".");
        var local = new TransportRegistry();
        local.Register(localHost);

        var factoryRegistry = new TransportFactoryRegistry();
        factoryRegistry.Register(new LocalTransportFactory(local));

        var router = new ExecutorTargetRouter(ExecutorTopology.SingleMachine, factoryRegistry);

        var machine = await OpenAndReadMachineAsync(
            router,
            TransportScenarioSupport.Json("""{"type":"local"}"""),
            McpTool("local-server", "https://local/mcp"),
            ct);

        Assert.Equal(".", machine);
        Assert.True(localHost.Invoked);
    }

    private static async Task<string?> OpenAndReadMachineAsync(
        ExecutorTargetRouter router,
        JsonElement descriptor,
        McpTool tool,
        CancellationToken ct)
    {
        await using var transport = await router.ConnectToDescriptorAsync(descriptor, ct);
        await using var client = new McpClientOverTransport(transport, McpConnectionRequest.FromTool(tool));
        await client.OpenAsync(ct);
        var reply = await client.ReadAsync(ct);
        return reply.GetProperty("machine").GetString();
    }

    private static PhantomMcpTool McpTool(string serverName, string endpoint)
        => new()
        {
            ServerName = serverName,
            Connection = new AnonymousConnection { Endpoint = endpoint },
        };

    private static JsonElement ProfileDescriptor(string machine)
        => TransportScenarioSupport.Json($$"""{"type":"user-computer-profile","entity-id":"{{machine}}"}""");

    private sealed class MachineRoutingTransportFactory : ITransportFactory
    {
        private readonly IReadOnlyDictionary<string, TransportRegistry> machines;

        public MachineRoutingTransportFactory(IReadOnlyDictionary<string, TransportRegistry> machines)
        {
            this.machines = machines;
        }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            if (connectionDescriptor.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "user-computer-profile", StringComparison.OrdinalIgnoreCase)
                && connectionDescriptor.TryGetProperty("entity-id", out var instance)
                && instance.GetString() is { } clientInstance
                && this.machines.TryGetValue(clientInstance, out var registry))
            {
                return Task.FromResult<ITransport?>(new LocalTransport(registry));
            }

            return Task.FromResult<ITransport?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An MCP listener that records invocation and replies with its machine tag and server name.</summary>
    private sealed class RecordingMcpListener : ITransportListener
    {
        private readonly string machine;

        public RecordingMcpListener(string machine)
        {
            this.machine = machine;
        }

        public bool Invoked { get; private set; }

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (request.ValueKind != JsonValueKind.Object
                || !request.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "mcp", StringComparison.OrdinalIgnoreCase)
                || !request.TryGetProperty("connection", out _))
            {
                return null;
            }

            this.Invoked = true;
            var reply = TransportScenarioSupport.Json($$"""{"type":"tool-ready","machine":"{{this.machine}}"}""");
            await channel.Writer.WriteAsync(reply, ct).ConfigureAwait(false);
            return new NoOpDisposable();
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
