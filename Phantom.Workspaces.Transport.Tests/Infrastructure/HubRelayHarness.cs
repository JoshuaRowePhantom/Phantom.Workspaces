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

    /// <summary>Default hub URL used by <see cref="ConnectMachineBAsync"/> and <see cref="CreateForwardingFactory"/>.</summary>
    public const string DefaultHubUrl = "https://hub-a.example";

    /// <summary>The registration channel serviced on the executor (Machine C) side.</summary>
    public IMessageChannel ExecutorRegistrationChannel { get; private set; } = null!;

    public static async Task<HubRelayHarness> CreateAsync(IChatClient executorChatClient, CancellationToken ct)
    {
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(executorChatClient));
        return await CreateAsync(registry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reusable overload (#1083): the caller supplies a fully-populated <see cref="TransportRegistry"/>
    /// hosting whatever listeners the executor should service (chat, shell, …). Everything else — the
    /// in-process hub fixture, the executor registration channel, and the reverse-execution dispatcher
    /// wiring — is set up identically to the single-<see cref="IChatClient"/> overload.
    /// </summary>
    public static async Task<HubRelayHarness> CreateAsync(TransportRegistry executorRegistry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executorRegistry);
        var fixture = new InProcessReverseHubFixture();
        var harness = new HubRelayHarness(fixture, Guid.NewGuid());
        harness.ownedAsync.Add(fixture);

        await fixture.SimulateClientRegistrationAsync(harness.ExecutorEntityId, ct).ConfigureAwait(false);
        harness.ExecutorRegistrationChannel = fixture.LastClientRegistrationChannel!;

        var dispatcher = new ReverseExecutionDispatcher(harness.ExecutorRegistrationChannel, executorRegistry);
        harness.ownedAsync.Insert(0, dispatcher);
        return harness;
    }

    /// <summary>
    /// Builds a real <see cref="ReverseHttpForwardingTransportFactory"/> wired to this harness's
    /// in-process hub shim. Callers can register the returned factory in their own
    /// <see cref="TransportFactoryRegistry"/> (e.g. behind a
    /// <see cref="UserComputerProfileTransportFactory"/>) so a
    /// <c>{"type":"reverse-http","hub-urls":[...],"entity-id":...}</c> descriptor routes through
    /// the executor without directly calling <see cref="ConnectMachineBAsync"/>.
    /// </summary>
    public ITransportFactory CreateForwardingFactory(
        params (string Url, InProcessHubHttpTransportFactory.HubBehavior Behavior)[] hubs)
    {
        if (hubs.Length == 0)
        {
            hubs = [(DefaultHubUrl, InProcessHubHttpTransportFactory.HubBehavior.Healthy)];
        }

        var shim = new InProcessHubHttpTransportFactory(this.Fixture);
        foreach (var (url, behavior) in hubs)
        {
            shim.SetBehavior(url, behavior);
        }

        var forwarding = new ReverseHttpForwardingTransportFactory(shim, TimeSpan.FromSeconds(20));
        this.ownedAsync.Add(forwarding);
        return forwarding;
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
            hubs = [(DefaultHubUrl, InProcessHubHttpTransportFactory.HubBehavior.Healthy)];
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

    /// <summary>
    /// Simulates Machine C crashing by tearing down its registration channel. The relay pump on the
    /// hub observes the closed executor side and closes Machine B's relayed channel in turn.
    /// </summary>
    public async ValueTask CrashExecutorAsync()
    {
        await this.ExecutorRegistrationChannel.DisposeAsync().ConfigureAwait(false);
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
