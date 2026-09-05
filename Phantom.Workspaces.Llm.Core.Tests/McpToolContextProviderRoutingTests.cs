using System.Text.Json;
using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Mcp;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// End-to-end routing tests for the per-component executor binding of an MCP tool (issue #1438).
/// A remote-bound <see cref="McpToolContextProvider"/> must route its MCP channel through the
/// production <see cref="ExecutorTargetRouter"/> and speak JSON-RPC over the channel via
/// <see cref="McpChannelClientTransport"/>, reaching a host that bridges to a real MCP server; a
/// locally bound provider must keep the in-process path and never touch the router. The host bridge
/// mirrors the production <c>RemoteMcpHostHandler</c> (which lives in the app assembly) so the whole
/// remote path is exercised with a real in-memory MCP server.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpToolContextProviderRoutingTests
{
    [Fact]
    public async Task BoundLocal_UsesInProcessFactory_NoRoundTrip()
    {
        await using var server = await InProcessMcpServer.StartAsync(new AsyncBarrier(1));
        var factory = new CountingTransportFactory(new TransportRegistry());
        var router = BuildRouter(factory);

        var provider = new McpToolContextProvider(
            RemoteTool(server),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            boundExecutor: LocalDescriptor(),
            router: router);

        var tools = await GetToolsAsync(provider);

        Assert.Contains(tools, tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task BoundRemote_ConnectsViaRouter()
    {
        await using var server = await InProcessMcpServer.StartAsync(new AsyncBarrier(1));
        var host = BuildHost(server);
        var factory = new CountingTransportFactory(host);
        var router = BuildRouter(factory);

        var provider = new McpToolContextProvider(
            RemoteTool(server),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            boundExecutor: RemoteDescriptor(),
            router: router);

        var tools = await GetToolsAsync(provider);

        Assert.Contains(tools, tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BoundRemote_ExecutorTargetRouterExercisedAsProductionConsumer()
    {
        await using var server = await InProcessMcpServer.StartAsync(new AsyncBarrier(1));
        var host = BuildHost(server);
        var factory = new CountingTransportFactory(host);
        var router = BuildRouter(factory);

        var provider = new McpToolContextProvider(
            RemoteTool(server),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            boundExecutor: RemoteDescriptor(),
            router: router);

        await GetToolsAsync(provider);

        // The router fed the bound connection-descriptor straight into the registry (no string hop).
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal("user-computer-profile", factory.LastDescriptor.GetProperty("type").GetString());
    }

    [Fact]
    public async Task BoundRemote_BridgesChannelViaMcpChannelClientTransport()
    {
        await using var server = await InProcessMcpServer.StartAsync(new AsyncBarrier(1));
        var host = BuildHost(server);
        var factory = new CountingTransportFactory(host);
        var router = BuildRouter(factory);

        var provider = new McpToolContextProvider(
            RemoteTool(server),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            boundExecutor: RemoteDescriptor(),
            router: router);

        var tools = await GetToolsAsync(provider);
        var ping = Assert.IsAssignableFrom<AIFunction>(
            tools.Single(tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase)));

        // A full tool call round-trips its payload over the bridged channel: the pong reply proves
        // McpChannelClientTransport pumped both the request and the response through the transport.
        var result = await ping.InvokeAsync(new AIFunctionArguments { ["message"] = "bridge" });
        Assert.Contains("pong", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenario3_RemoteBoundMcpServer_ToolError_RoundTripsError()
    {
        await using var server = await InProcessMcpServer.StartAsync(new AsyncBarrier(1));
        var host = BuildHost(server);
        var factory = new CountingTransportFactory(host);
        var router = BuildRouter(factory);

        var provider = new McpToolContextProvider(
            RemoteTool(server),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            boundExecutor: RemoteDescriptor(),
            router: router);

        var tools = await GetToolsAsync(provider);
        var fail = Assert.IsAssignableFrom<AIFunction>(
            tools.Single(tool => string.Equals(tool.Name, "fail", StringComparison.OrdinalIgnoreCase)));

        // A FAILING remote tool call must round-trip the server's tool-error back to the caller over
        // the bridged channel — not be thrown locally before the round-trip. The MCP SDK reports a
        // server-side tool failure as an `isError` result (the exception detail is intentionally not
        // leaked to the client), so a truthful `isError` here proves the error travelled back through
        // McpChannelClientTransport rather than being a local invocation failure.
        var result = await fail.InvokeAsync(new AIFunctionArguments { ["message"] = "bridge" });
        var surfaced = JsonSerializer.Serialize(result);

        using var document = JsonDocument.Parse(surfaced);
        Assert.True(
            document.RootElement.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True,
            $"Expected the remote tool failure to round-trip as an isError result, but got: {surfaced}");

        // The error content came back over the channel and names the failing tool, confirming this is
        // the round-tripped tool-error and not an empty/success payload.
        Assert.Contains("fail", surfaced, StringComparison.OrdinalIgnoreCase);
    }

    // Mirrors the production RemoteMcpHostHandler (app assembly): opens the requested MCP server via
    // the shared factory and bridges its JSON-RPC to the incoming channel with a DelegatingMcpServer.
    private static TransportRegistry BuildHost(InProcessMcpServer server)
    {
        _ = server;
        var registry = new TransportRegistry();
        registry.Register(new McpTransportListener(async (request, channel, ct) =>
        {
            var tool = McpConnectionRequest.ToTool(request);
            if (tool is null)
            {
                return null;
            }

            var serverTransport = await McpTransportFactory.CreateMcpTransportAsync(tool, null, NullLoggerFactory.Instance, ct);
            var delegatingServer = new DelegatingMcpServer(serverTransport);
            var incoming = McpChannelClientTransport.CreateServerTransport(channel);
            var cts = new CancellationTokenSource();
            var relay = Task.Run(() => delegatingServer.RunAsync(incoming, cts.Token), CancellationToken.None);
            return new HostSession(delegatingServer, incoming, cts, relay);
        }));
        return registry;
    }

    private static ExecutorTargetRouter BuildRouter(CountingTransportFactory factory)
    {
        var factoryRegistry = new TransportFactoryRegistry();
        factoryRegistry.Register(factory);
        return new ExecutorTargetRouter(ExecutorTopology.SingleMachine, factoryRegistry);
    }

    private static PhantomMcpTool RemoteTool(InProcessMcpServer server)
        => new()
        {
            ServerName = "remote-mcp",
            Connection = new AnonymousConnection { Endpoint = server.BoundUrl },
        };

    private static JsonElement LocalDescriptor()
        => JsonDocument.Parse("""{"type":"local"}""").RootElement.Clone();

    private static JsonElement RemoteDescriptor()
        => JsonDocument.Parse("""{"type":"user-computer-profile","entity-id":"remote"}""").RootElement.Clone();

    private static async Task<AITool[]> GetToolsAsync(McpToolContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }

    private sealed class CountingTransportFactory : ITransportFactory
    {
        private readonly TransportRegistry registry;

        public CountingTransportFactory(TransportRegistry registry)
        {
            this.registry = registry;
        }

        public int ConnectCount { get; private set; }

        public JsonElement LastDescriptor { get; private set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.ConnectCount++;
            this.LastDescriptor = connectionDescriptor.Clone();
            return Task.FromResult<ITransport?>(new LocalTransport(this.registry));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HostSession(
        DelegatingMcpServer delegatingServer,
        ModelContextProtocol.Protocol.ITransport incoming,
        CancellationTokenSource cts,
        Task relay) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await incoming.DisposeAsync().ConfigureAwait(false);
            try
            {
                await relay.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await delegatingServer.DisposeAsync().ConfigureAwait(false);
            cts.Dispose();
        }
    }
}
