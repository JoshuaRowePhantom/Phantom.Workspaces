using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Lease-expiry: a server-side lease that fires mid-turn (while a streaming response is in flight)
/// must tear the underlying transport down so the caller's
/// <see cref="ChatClientOverTransport.GetStreamingResponseAsync"/> terminates promptly with an
/// exception rather than hanging silently. Uses an injectable clock — no wall-clock delays.
/// </summary>
public sealed class LeaseExpiryTests
{
    [Fact]
    public async Task LeaseExpiry_MidTurn_TerminatesStreamingWithoutHang()
    {
        var ct = TransportScenarioSupport.TestToken();
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lease = TimeSpan.FromSeconds(90);
        await using var httpServer = new InProcessHttpServerTransportFactory(lease, () => now);

        // The executor streams one update and then stalls (the second update is never marked ready
        // and the turn is never completed), holding the turn in flight across the lease boundary.
        var executor = new DeterministicTestChatClient();
        var stream = executor.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "partial"));
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "never"), isReady: false);

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        var (server, client) = InProcessTransport.Create(registry);
        await httpServer.AcceptAsync(server, ct);

        using var over = new ChatClientOverTransport(client, TransportScenarioSupport.ChatClientRequest());
        await using var enumerator = over
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], null, ct)
            .GetAsyncEnumerator(ct);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("partial", enumerator.Current.Text);

        // Advance the clock past the lease and sweep: the accepted transport is disposed mid-turn.
        now += lease + TimeSpan.FromSeconds(1);
        await httpServer.SweepExpiredLeasesAsync(ct);

        // The turn must not hang: continued enumeration observes the closed channel and throws.
        await Assert.ThrowsAnyAsync<Exception>(async () => await enumerator.MoveNextAsync());
        Assert.Equal(0, httpServer.ActiveTransportCount);
    }

    [Fact]
    public async Task LeaseExpiry_BeforeExpiry_TurnCompletesNormally()
    {
        var ct = TransportScenarioSupport.TestToken();
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lease = TimeSpan.FromSeconds(90);
        await using var httpServer = new InProcessHttpServerTransportFactory(lease, () => now);

        var executor = TransportScenarioSupport.StreamingChatClient("done");
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executor));
        var (server, client) = InProcessTransport.Create(registry);
        await httpServer.AcceptAsync(server, ct);

        using var over = new ChatClientOverTransport(client, TransportScenarioSupport.ChatClientRequest());

        // Advance, but not past the lease: the sweep must not tear down the in-lease transport.
        now += TimeSpan.FromSeconds(30);
        await httpServer.SweepExpiredLeasesAsync(ct);

        Assert.Equal("done", await TransportScenarioSupport.RunTurnAsync(over, "hi", ct));
        Assert.Equal(1, httpServer.ActiveTransportCount);
    }
}
