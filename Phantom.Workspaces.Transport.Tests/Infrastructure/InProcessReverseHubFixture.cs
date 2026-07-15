using System.Text.Json;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

public sealed class InProcessReverseHubFixture : IAsyncDisposable
{
    private readonly List<ITransport> transports = [];
    private readonly List<IMessageChannel> registrationChannels = [];
    private readonly List<IAsyncDisposable> leases = [];

    public InProcessReverseHubFixture()
    {
        this.HttpServer = new InProcessHttpServerTransportFactory();
        this.HubRegistry = this.HttpServer.Registry;
        this.ReverseHttpServer = new ReverseHttpServerTransportFactory();
        this.HubRegistry.Register(this.ReverseHttpServer);
    }

    public InProcessHttpServerTransportFactory HttpServer { get; }

    public TransportRegistry HubRegistry { get; }

    public ReverseHttpServerTransportFactory ReverseHttpServer { get; }

    public IMessageChannel? LastClientRegistrationChannel { get; private set; }

    public async Task<ITransport> SimulateClientRegistrationAsync(Guid machineEntityId, CancellationToken ct = default)
    {
        var (server, client) = InProcessTransport.Create(this.HubRegistry);
        this.transports.Add(server);
        this.transports.Add(client);
        await this.HttpServer.AcceptAsync(server, ct).ConfigureAwait(false);
        using var registerDescriptor = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "reverse-register",
            ["entity-id"] = machineEntityId.ToString("D"),
        }));
        var channel = await client.ConnectToMessageChannelAsync(registerDescriptor.RootElement, ct).ConfigureAwait(false);
        this.registrationChannels.Add(channel);
        this.LastClientRegistrationChannel = channel;
        await this.WaitUntilRegisteredAsync(machineEntityId.ToString("D"), ct).ConfigureAwait(false);
        return client;
    }

    public async Task<ITransport> CreateForwardingClientAsync(CancellationToken ct = default)
    {
        var (server, client) = InProcessTransport.Create(this.HubRegistry);
        this.transports.Add(server);
        this.transports.Add(client);
        await this.HttpServer.AcceptAsync(server, ct).ConfigureAwait(false);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lease in this.leases)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var channel in this.registrationChannels)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var transport in this.transports)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        await this.ReverseHttpServer.DisposeAsync().ConfigureAwait(false);
        await this.HttpServer.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WaitUntilRegisteredAsync(string entityId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!this.ReverseHttpServer.IsRegistered(entityId))
        {
            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }
}
