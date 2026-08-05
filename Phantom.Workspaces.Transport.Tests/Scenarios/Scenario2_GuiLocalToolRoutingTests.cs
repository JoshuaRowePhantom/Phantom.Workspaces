using System.Text.Json;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Scenario 2: a <c>workspace-gui</c> (gui-local) tool invoked during a remote agent turn is routed
/// back to and executed on the initiating GUI machine (Machine A), not on the remote executor
/// (Machine C). This completes the cross-machine tool-routing assertion that #984 deferred pending
/// the #983 per-tool <see cref="ExecutorTarget"/> routing work. Everything is hermetic and
/// deterministic: machines are in-process <see cref="TransportRegistry"/> instances reached over
/// <see cref="LocalTransport"/>, and routing is driven by <see cref="ExecutorTargetRouter"/>.
/// </summary>
public sealed class Scenario2_GuiLocalToolRoutingTests
{
    [Fact]
    public async Task Scenario2_GuiLocalTool_DuringRemoteTurn_RoutesBackToMachineA()
    {
        var ct = TransportScenarioSupport.TestToken();

        // Machine A is the initiating GUI machine; it hosts the gui-local (workspace-gui) tool.
        var guiToolOnA = new RecordingToolListener("A");
        var machineA = new TransportRegistry();
        machineA.Register(guiToolOnA);

        // Machine C is the remote executor: it runs the agent turn and hosts a gui tool that must NOT run.
        var executor = TransportScenarioSupport.StreamingChatClient("remote-", "turn");
        var guiToolOnC = new RecordingToolListener("C");
        var machineC = new TransportRegistry();
        machineC.Register(new ChatClientTransportListener(executor));
        machineC.Register(guiToolOnC);

        var factoryRegistry = new TransportFactoryRegistry();
        factoryRegistry.Register(new MachineRoutingTransportFactory(new Dictionary<string, TransportRegistry>(StringComparer.Ordinal)
        {
            ["A"] = machineA,
            ["C"] = machineC,
        }));

        var topology = new ExecutorTopology
        {
            GuiLocalClientInstance = "A",
            AgentExecutorClientInstance = "C",
            HostingInstanceClientInstance = "A",
        };
        var router = new ExecutorTargetRouter(topology, factoryRegistry);

        // The agent turn runs on the remote executor (Machine C).
        await using var executorTransport = await router.ConnectAsync(ExecutorTarget.AgentExecutor, ct);
        using (var chatClient = new ChatClientOverTransport(executorTransport, TransportScenarioSupport.ChatClientRequest()))
        {
            var text = await TransportScenarioSupport.RunTurnAsync(chatClient, "go", ct);
            Assert.Equal("remote-turn", text);
        }

        // A workspace-gui (gui-local) tool invoked during that turn routes back to Machine A.
        await using var guiTransport = await router.ConnectAsync(ExecutorTarget.GuiLocal, ct);
        await using var guiChannel = await guiTransport.ConnectToMessageChannelAsync(GuiToolRequest(), ct);
        var reply = await guiChannel.Reader.ReadAsync(ct);

        Assert.Equal("A", reply.GetProperty("machine").GetString());
        Assert.Equal("A", router.ResolveClientInstance(ExecutorTarget.GuiLocal));
        Assert.True(guiToolOnA.Invoked);
        Assert.False(guiToolOnC.Invoked);
    }

    [Fact]
    public async Task Scenario2_SingleMachineTopology_GuiLocalTool_ResolvesLocalNoInstanceHop()
    {
        var ct = TransportScenarioSupport.TestToken();

        var guiTool = new RecordingToolListener(".");
        var local = new TransportRegistry();
        local.Register(guiTool);

        var factoryRegistry = new TransportFactoryRegistry();
        factoryRegistry.Register(new LocalTransportFactory(local));

        var router = new ExecutorTargetRouter(ExecutorTopology.SingleMachine, factoryRegistry);

        // Single-machine topology resolves gui-local to the local descriptor: no user-computer-profile hop.
        Assert.Equal("local", router.ResolveDescriptor(ExecutorTarget.GuiLocal).GetProperty("type").GetString());
        Assert.True(router.ResolvesLocally(ExecutorTarget.GuiLocal));

        await using var transport = await router.ConnectAsync(ExecutorTarget.GuiLocal, ct);
        await using var channel = await transport.ConnectToMessageChannelAsync(GuiToolRequest(), ct);
        var reply = await channel.Reader.ReadAsync(ct);

        Assert.Equal(".", reply.GetProperty("machine").GetString());
        Assert.True(guiTool.Invoked);
    }

    private static JsonElement GuiToolRequest()
        => TransportScenarioSupport.Json("""{"type":"mcp","connection":{"tool":"workspace-gui"}}""");

    /// <summary>
    /// Routes a resolved connection descriptor to the in-process <see cref="TransportRegistry"/> of the
    /// named machine. Handles the <c>user-computer-profile</c> descriptor produced by
    /// <see cref="Phantom.Workspaces.Llm.Trust.ExecutionTargetResolver"/> for a remote client instance.
    /// </summary>
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

    /// <summary>An MCP-tool listener that records that it was invoked and replies with its machine tag.</summary>
    private sealed class RecordingToolListener : ITransportListener
    {
        private readonly string machine;

        public RecordingToolListener(string machine)
        {
            this.machine = machine;
        }

        public bool Invoked { get; private set; }

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (request.ValueKind != JsonValueKind.Object
                || !request.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "mcp", StringComparison.OrdinalIgnoreCase))
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
