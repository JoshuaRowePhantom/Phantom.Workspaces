using System.Text.Json;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ExecutorTargetRouterTests
{
    [Fact]
    public void ResolveDescriptor_SingleMachineTarget_IsLocal()
    {
        var router = new ExecutorTargetRouter(ExecutorTopology.SingleMachine, new RecordingRegistry());

        var descriptor = router.ResolveDescriptor(ExecutorTarget.AgentExecutor);

        Assert.Equal("local", descriptor.GetProperty("type").GetString());
        Assert.False(descriptor.TryGetProperty("target-client-instance", out _));
    }

    [Fact]
    public void ResolveDescriptor_RemoteTarget_CarriesTargetClientInstance()
    {
        var topology = new ExecutorTopology { GuiLocalClientInstance = "G" };
        var router = new ExecutorTargetRouter(topology, new RecordingRegistry());

        var descriptor = router.ResolveDescriptor(ExecutorTarget.GuiLocal);

        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal("G", descriptor.GetProperty("target-client-instance").GetString());
    }

    [Fact]
    public async Task ConnectAsync_RemoteGuiLocal_ConnectsWithGuiInstanceDescriptor()
    {
        var registry = new RecordingRegistry();
        var topology = new ExecutorTopology
        {
            AgentExecutorClientInstance = "E",
            GuiLocalClientInstance = "G",
        };
        var router = new ExecutorTargetRouter(topology, registry);

        var transport = await router.ConnectAsync(ExecutorTarget.GuiLocal, CancellationToken.None);

        Assert.Same(registry.Transport, transport);
        Assert.NotNull(registry.LastDescriptor);
        Assert.Equal("user-computer-profile", registry.LastDescriptor!.Value.GetProperty("type").GetString());
        Assert.Equal("G", registry.LastDescriptor!.Value.GetProperty("target-client-instance").GetString());
    }

    [Fact]
    public async Task ConnectAsync_SingleMachine_ConnectsLocallyWithNoInstanceHop()
    {
        var registry = new RecordingRegistry();
        var router = new ExecutorTargetRouter(ExecutorTopology.SingleMachine, registry);

        await router.ConnectAsync(ExecutorTarget.AgentExecutor, CancellationToken.None);

        Assert.True(router.ResolvesLocally(ExecutorTarget.AgentExecutor));
        Assert.Equal("local", registry.LastDescriptor!.Value.GetProperty("type").GetString());
    }

    private sealed class RecordingRegistry : ITransportFactoryRegistry
    {
        public JsonElement? LastDescriptor { get; private set; }

        public ITransport Transport { get; } = new NoOpTransport();

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.LastDescriptor = connectionDescriptor.Clone();
            return Task.FromResult(this.Transport);
        }
    }

    private sealed class NoOpTransport : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
