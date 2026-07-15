using System.Text.Json;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpClientTransportFactory : ITransportFactory
{
    private readonly HttpClientTransportFactory httpClientTransportFactory;
    private readonly string hubUrl;
    private readonly string entityId;
    private ITransport? hubTransport;
    private IMessageChannel? registrationChannel;

    public ReverseHttpClientTransportFactory(string hubUrl, string entityId)
        : this(new HttpClientTransportFactory(), hubUrl, entityId)
    {
    }

    public ReverseHttpClientTransportFactory(HttpClientTransportFactory httpClientTransportFactory, string hubUrl, string entityId)
    {
        this.httpClientTransportFactory = httpClientTransportFactory;
        this.hubUrl = hubUrl;
        this.entityId = entityId;
    }

    public string HubUrl => this.hubUrl;

    public string EntityId => this.entityId;

    public async Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "reverse-http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (connectionDescriptor.TryGetProperty("entity-id", out var descriptorEntityId)
            && descriptorEntityId.GetString() is { Length: > 0 } requestedEntityId
            && !string.Equals(requestedEntityId, this.entityId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var channel = await this.EnsureRegisteredAsync(ct).ConfigureAwait(false);
        return new ReverseHttpTransport(channel);
    }

    public async Task<IMessageChannel> EnsureRegisteredAsync(CancellationToken ct = default)
    {
        if (this.registrationChannel is not null)
        {
            return this.registrationChannel;
        }

        using var hubDescriptor = JsonDocument.Parse($$"""{"type":"http","url":"{{this.hubUrl}}"}""");
        this.hubTransport = await this.httpClientTransportFactory.ConnectToAsync(hubDescriptor.RootElement, ct).ConfigureAwait(false)
            ?? throw new TransportException("HTTP client transport factory did not handle the hub descriptor.");
        using var registerDescriptor = JsonDocument.Parse($$"""{"type":"reverse-register","entity-id":"{{this.entityId}}"}""");
        this.registrationChannel = await this.hubTransport.ConnectToMessageChannelAsync(registerDescriptor.RootElement, ct).ConfigureAwait(false);
        return this.registrationChannel;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.registrationChannel is not null)
        {
            await this.registrationChannel.DisposeAsync().ConfigureAwait(false);
        }

        if (this.hubTransport is not null)
        {
            await this.hubTransport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
