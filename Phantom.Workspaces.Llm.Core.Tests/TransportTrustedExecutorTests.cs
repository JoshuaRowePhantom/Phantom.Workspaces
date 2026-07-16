using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class TransportTrustedExecutorTests
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

    private static AgentSchema.AgentDefinition EchoAgent()
        => AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);

    [Fact]
    public void CanExecute_ResolvableTarget_ReturnsTrue()
    {
        var registry = new FakeTransportFactoryRegistry(new FakeTransport());
        var executor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        Assert.True(executor.CanExecute("."));
        Assert.True(executor.CanExecute("remote-a"));
        Assert.False(executor.CanExecute("   "));
    }

    [Fact]
    public async Task CreateAgentChat_LocalProfile_UsesLocalTransport()
    {
        var registry = new FakeTransportFactoryRegistry(new FakeTransport());
        await using var executor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        await using var chat = await executor.CreateAgentChatAsync(new TrustedExecutionRequest
        {
            AgentDefinition = EchoAgent(),
            TrustProfile = new TrustProfile { HostingWorkspacesClientInstances = ["."] },
            TargetClientInstance = TrustProfile.LocalClientInstance,
        });

        Assert.NotNull(chat);
        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("local", descriptor.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CreateAgentChat_RemoteProfile_UsesTransportFactoryRegistry()
    {
        var registry = new FakeTransportFactoryRegistry(new FakeTransport());
        await using var executor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        await using var chat = await executor.CreateAgentChatAsync(new TrustedExecutionRequest
        {
            AgentDefinition = EchoAgent(),
            TrustProfile = new TrustProfile { HostingWorkspacesClientInstances = ["remote-a"] },
            TargetClientInstance = "remote-a",
        });

        Assert.NotNull(chat);
        var descriptor = Assert.Single(registry.Descriptors);
        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal("remote-a", descriptor.GetProperty("target-client-instance").GetString());
    }

    [Fact]
    public async Task OpenStream_ShellTarget_ReturnsShellOverTransportStream()
    {
        var transportStream = new MemoryStream();
        var transport = new FakeTransport { StreamToReturn = transportStream };
        var registry = new FakeTransportFactoryRegistry(transport);
        await using var executor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        var stream = await executor.OpenStreamAsync(new TrustedStreamRequest
        {
            TargetClientInstance = "remote-a",
            StreamKind = "shell",
            OpenPayload = JsonDocument.Parse("""{"command":"bash"}""").RootElement,
        });

        Assert.Same(transportStream, stream);
        Assert.NotNull(transport.LastStreamRequest);
        var request = transport.LastStreamRequest!.Value;
        Assert.Equal("shell", request.GetProperty("type").GetString());
        Assert.Equal("bash", request.GetProperty("command").GetString());
    }

    [Fact]
    public async Task RunTool_McpTarget_RoundTripsViaMcpClientOverTransport()
    {
        var channel = new FakeMessageChannel();
        channel.SeedInbound(JsonDocument.Parse("""{"type":"tool-complete"}""").RootElement);
        var transport = new FakeTransport { ChannelToReturn = channel };
        var registry = new FakeTransportFactoryRegistry(transport);
        await using var executor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        await executor.RunToolAsync(new TrustedToolRequest
        {
            ToolTypeName = "git-workspace-scan",
            ToolEntityId = "entity-1",
            TargetClientInstance = "remote-a",
        });

        var sent = Assert.Single(channel.Sent);
        Assert.Equal("run-tool", sent.GetProperty("type").GetString());
        Assert.Equal("git-workspace-scan", sent.GetProperty("tool-type-name").GetString());
        Assert.Equal("entity-1", sent.GetProperty("tool-entity-id").GetString());
    }

    [Fact]
    public async Task RunTool_McpTarget_ToolError_ThrowsTransportException()
    {
        var channel = new FakeMessageChannel();
        channel.SeedInbound(JsonDocument.Parse("""{"type":"tool-error","message":"denied"}""").RootElement);
        var transport = new FakeTransport { ChannelToReturn = channel };
        var registry = new FakeTransportFactoryRegistry(transport);
        await using var executor = new TransportTrustedExecutor(registry, new ExecutionTargetResolver());

        var exception = await Assert.ThrowsAsync<TransportException>(() => executor.RunToolAsync(new TrustedToolRequest
        {
            ToolTypeName = "git-workspace-scan",
            ToolEntityId = "entity-1",
            TargetClientInstance = "remote-a",
        }));

        Assert.Contains("denied", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeTransportFactoryRegistry : ITransportFactoryRegistry
    {
        private readonly ITransport transport;

        public FakeTransportFactoryRegistry(ITransport transport) => this.transport = transport;

        public List<JsonElement> Descriptors { get; } = new();

        public void Register(ITransportFactory factory)
        {
        }

        public Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.Descriptors.Add(connectionDescriptor.Clone());
            return Task.FromResult(this.transport);
        }
    }

    private sealed class FakeTransport : ITransport
    {
        public IMessageChannel? ChannelToReturn { get; init; }

        public Stream? StreamToReturn { get; init; }

        public JsonElement? LastStreamRequest { get; private set; }

        public JsonElement? LastMessageRequest { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.LastMessageRequest = request.Clone();
            return Task.FromResult(this.ChannelToReturn ?? new FakeMessageChannel());
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
        {
            this.LastStreamRequest = request.Clone();
            return Task.FromResult(this.StreamToReturn ?? Stream.Null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> outbound = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public List<JsonElement> Sent
        {
            get
            {
                var sent = new List<JsonElement>();
                while (this.outbound.Reader.TryRead(out var frame))
                {
                    sent.Add(frame);
                }

                return sent;
            }
        }

        public void SeedInbound(JsonElement frame) => this.inbound.Writer.TryWrite(frame.Clone());

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            this.outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
