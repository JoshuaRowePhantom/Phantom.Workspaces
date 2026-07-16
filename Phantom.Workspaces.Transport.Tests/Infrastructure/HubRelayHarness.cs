using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

/// <summary>
/// A three-machine hub-relay harness: Machine A is the in-process hub, Machine C is a registered
/// executor serviced by a <see cref="ReverseExecutionDispatcher"/>, and Machine B connects to C
/// through the hub using the real <see cref="ReverseHttpForwardingTransportFactory"/> (over an
/// in-process HTTP shim). Everything is hermetic and deterministic.
/// </summary>
internal sealed class HubRelayHarness : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> ownedAsync = [];
    private readonly List<IDisposable> owned = [];

    private HubRelayHarness(InProcessReverseHubFixture fixture, Guid executorEntityId)
    {
        this.Fixture = fixture;
        this.ExecutorEntityId = executorEntityId;
    }

    public InProcessReverseHubFixture Fixture { get; }

    public Guid ExecutorEntityId { get; }

    /// <summary>The registration channel serviced on the executor (Machine C) side.</summary>
    public IMessageChannel ExecutorRegistrationChannel { get; private set; } = null!;

    public static async Task<HubRelayHarness> CreateAsync(IChatClient executorChatClient, CancellationToken ct)
    {
        var fixture = new InProcessReverseHubFixture();
        var harness = new HubRelayHarness(fixture, Guid.NewGuid());
        harness.ownedAsync.Add(fixture);

        await fixture.SimulateClientRegistrationAsync(harness.ExecutorEntityId, ct).ConfigureAwait(false);
        harness.ExecutorRegistrationChannel = fixture.LastClientRegistrationChannel!;

        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executorChatClient));
        var dispatcher = new ReverseExecutionDispatcher(harness.ExecutorRegistrationChannel, registry);
        harness.ownedAsync.Insert(0, dispatcher);
        return harness;
    }

    /// <summary>
    /// Connects Machine B to the executor through the hub. The supplied hub-URL behaviours drive
    /// hub-URL racing/fallback simulation; by default a single healthy hub URL is used.
    /// </summary>
    public async Task<ITransport> ConnectMachineBAsync(
        CancellationToken ct,
        params (string Url, InProcessHubHttpTransportFactory.HubBehavior Behavior)[] hubs)
    {
        if (hubs.Length == 0)
        {
            hubs = [("https://hub-a.example", InProcessHubHttpTransportFactory.HubBehavior.Healthy)];
        }

        var shim = new InProcessHubHttpTransportFactory(this.Fixture);
        foreach (var (url, behavior) in hubs)
        {
            shim.SetBehavior(url, behavior);
        }

        var forwarding = new ReverseHttpForwardingTransportFactory(shim, TimeSpan.FromSeconds(20));
        this.ownedAsync.Add(forwarding);

        var urlsJson = JsonSerializer.Serialize(hubs.Select(hub => hub.Url).ToArray());
        using var descriptor = JsonDocument.Parse(
            $$"""{"type":"reverse-http","hub-urls":{{urlsJson}},"entity-id":"{{this.ExecutorEntityId:D}}"}""");
        var transport = await forwarding.ConnectToAsync(descriptor.RootElement, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Forwarding factory did not produce a transport.");
        this.ownedAsync.Add(transport);
        return transport;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in this.owned)
        {
            disposable.Dispose();
        }

        foreach (var disposable in this.ownedAsync)
        {
            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
