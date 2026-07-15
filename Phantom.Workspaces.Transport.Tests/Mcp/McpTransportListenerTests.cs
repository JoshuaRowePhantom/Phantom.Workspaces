using System.Text.Json;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.Mcp;

namespace Phantom.Workspaces.Transport.Tests.Mcp;

public sealed class McpTransportListenerTests
{
    [Fact]
    public async Task McpTransportListener_NonMcpDescriptor_ReturnsNull()
    {
        await using var listener = new McpTransportListener((_, _, _) => Task.FromResult<IAsyncDisposable?>(new DummyDisposable()));
        var channel = await CreateChannelAsync();

        var result = await listener.OnChannelOpenAsync(Json("""{"type":"other"}"""), channel, TestCancellationToken());

        Assert.Null(result);
    }

    [Fact]
    public async Task McpTransportListener_Connection_BridgesProtocol()
    {
        var registry = new TransportRegistry();
        registry.Register(new McpTransportListener(async (request, channel, ct) =>
        {
            var pumpTask = Task.Run(async () =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(ct))
                {
                    _ = message;
                    await channel.Writer.WriteAsync(Json("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}"""), ct);
                }
            }, ct);
            return await Task.FromResult<IAsyncDisposable?>(new DummyDisposable());
        }));
        await using var transport = new LocalTransport(registry);
        await using var client = new McpClientOverTransport(transport, Json("""{"type":"mcp","connection":{"kind":"stdio"}}"""));

        await client.OpenAsync(TestCancellationToken());
        await client.SendAsync(Json("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), TestCancellationToken());
        var response = await client.ReadAsync(TestCancellationToken());

        Assert.Equal(1, response.GetProperty("id").GetInt32());
        Assert.True(response.GetProperty("result").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task McpTransportListener_Dispose_ClosesMcpSession()
    {
        var disposed = false;
        await using var listener = new McpTransportListener((_, _, _) => Task.FromResult<IAsyncDisposable?>(new CallbackDisposable(() => disposed = true)));
        var channel = await CreateChannelAsync();

        var session = await listener.OnChannelOpenAsync(Json("""{"type":"mcp","connection":{}}"""), channel, TestCancellationToken());
        await session!.DisposeAsync();

        Assert.True(disposed);
    }

    private static async Task<IMessageChannel> CreateChannelAsync()
    {
        var registry = new TransportRegistry();
        registry.Register(new HoldingListener());
        await using var transport = new LocalTransport(registry);
        return await transport.ConnectToMessageChannelAsync(Json("{}"));
    }

    private static CancellationToken TestCancellationToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private sealed class DummyDisposable : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class CallbackDisposable(Action callback) : IAsyncDisposable { public ValueTask DisposeAsync() { callback(); return ValueTask.CompletedTask; } }
    private sealed class HoldingListener : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default) => Task.FromResult<IAsyncDisposable?>(new DummyDisposable());
        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default) => Task.FromResult<IAsyncDisposable?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

