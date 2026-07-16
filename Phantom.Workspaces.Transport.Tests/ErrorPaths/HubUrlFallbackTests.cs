using System.Text.Json;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.ErrorPaths;

/// <summary>
/// Hub-URL fallback and rotation error paths, exercised end-to-end through the real
/// <see cref="ReverseHttpForwardingTransportFactory"/> over an in-process hub, plus the hub-URL
/// lifecycle on <see cref="ReverseHttpClientTransportFactory"/> across a crash/restart.
/// </summary>
public sealed class HubUrlFallbackTests
{
    [Fact]
    public async Task HubUrlFallback_HungHubRotatesToHealthy_TurnSucceedsWithoutError()
    {
        var ct = TransportScenarioSupport.TestToken();
        var executor = TransportScenarioSupport.StreamingChatClient("rotated-", "ok");
        await using var harness = await HubRelayHarness.CreateAsync(executor, ct);

        // The first (stale) hub URL hangs; the second is healthy. Racing must fall back to the
        // healthy hub so the turn still completes — only latency is affected, never correctness.
        var machineB = await harness.ConnectMachineBAsync(
            ct,
            ("https://stale-hub.example", InProcessHubHttpTransportFactory.HubBehavior.Hang),
            ("https://healthy-hub.example", InProcessHubHttpTransportFactory.HubBehavior.Healthy));
        using var client = new ChatClientOverTransport(machineB, TransportScenarioSupport.ChatClientRequest());

        var text = await TransportScenarioSupport.RunTurnAsync(client, "please respond", ct);

        Assert.Equal("rotated-ok", text);
    }

    [Fact]
    public async Task MachineCCrash_StaleHubUrl_ConnectionFailsWithTransportError()
    {
        var ct = TransportScenarioSupport.TestToken();
        await using var fixture = new InProcessReverseHubFixture();

        // Machine C has crashed. The only hub URL Machine B still holds is now stale — the hub no
        // longer accepts a relay for the crashed entity — so every connection attempt fails.
        var shim = new InProcessHubHttpTransportFactory(fixture);
        shim.SetBehavior("https://stale-hub.example", InProcessHubHttpTransportFactory.HubBehavior.Fail);
        await using var forwarding = new ReverseHttpForwardingTransportFactory(shim, TimeSpan.FromSeconds(20));
        using var descriptor = JsonDocument.Parse(
            $$"""{"type":"reverse-http","hub-urls":["https://stale-hub.example"],"entity-id":"{{Guid.NewGuid():D}}"}""");

        // The turn must not hang: exhausting the stale hub URLs surfaces a transport error promptly.
        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await forwarding.ConnectToAsync(descriptor.RootElement, ct));
        Assert.Contains("All reverse HTTP hub connection attempts failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MachineCRestart_HubUrls_ClearedBeforeEachRegister()
    {
        // First lifecycle: a fresh factory starts empty, registers, then is torn down (crash).
        var first = new ReverseHttpClientTransportFactory(
            new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory(),
            "https://hub.example",
            "machine-c");
        Assert.Empty(first.HubUrls);

        await first.EnsureRegisteredAsync();
        Assert.Equal(["https://hub.example"], first.HubUrls);

        await first.DisposeAsync();
        Assert.Empty(first.HubUrls);

        // Restart: a brand-new factory again starts with hub-urls cleared before it registers,
        // so no stale hub URL from the previous incarnation can leak into the restarted process.
        var restarted = new ReverseHttpClientTransportFactory(
            new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory(),
            "https://hub.example",
            "machine-c");
        Assert.Empty(restarted.HubUrls);

        await restarted.EnsureRegisteredAsync();
        Assert.Equal(["https://hub.example"], restarted.HubUrls);

        await restarted.DisposeAsync();
    }
}
