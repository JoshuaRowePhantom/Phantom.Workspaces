using System.Text.Json;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpClientTransportFactory : ITransportFactory
{
    private readonly ITransportFactory httpClientTransportFactory;
    private readonly string hubUrl;
    private readonly string entityId;
    private readonly List<string> hubUrls = [];
    private ITransport? hubTransport;
    private IMessageChannel? registrationChannel;

    public ReverseHttpClientTransportFactory(string hubUrl, string entityId)
        : this(new HttpClientTransportFactory(), hubUrl, entityId)
    {
    }

    public ReverseHttpClientTransportFactory(HttpClientTransportFactory httpClientTransportFactory, string hubUrl, string entityId)
        : this((ITransportFactory)httpClientTransportFactory, hubUrl, entityId)
    {
    }

    public ReverseHttpClientTransportFactory(ITransportFactory httpClientTransportFactory, string hubUrl, string entityId)
    {
        this.httpClientTransportFactory = httpClientTransportFactory;
        this.hubUrl = hubUrl;
        this.entityId = entityId;
    }

    public string HubUrl => this.hubUrl;

    public string EntityId => this.entityId;

    public IReadOnlyList<string> HubUrls => this.hubUrls;

    public static TimeSpan GetReconnectDelayForAttempt(int attempt, double jitterFactor = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var seconds = Math.Min(Math.Pow(2, attempt - 1), 60);
        return TimeSpan.FromSeconds(seconds * jitterFactor);
    }

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
        this.UpsertHubUrl();
        return this.registrationChannel;
    }

    public async Task<IMessageChannel> ReconnectAsync(CancellationToken ct = default)
    {
        if (this.registrationChannel is not null)
        {
            await this.registrationChannel.DisposeAsync().ConfigureAwait(false);
            this.registrationChannel = null;
        }

        if (this.hubTransport is not null)
        {
            await this.hubTransport.DisposeAsync().ConfigureAwait(false);
            this.hubTransport = null;
        }

        return await this.EnsureRegisteredAsync(ct).ConfigureAwait(false);
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

        this.hubUrls.Clear();
        await this.httpClientTransportFactory.DisposeAsync().ConfigureAwait(false);
    }

    private void UpsertHubUrl()
    {
        if (this.hubUrls.Count == 0)
        {
            this.hubUrls.Add(this.hubUrl);
        }
        else
        {
            this.hubUrls[0] = this.hubUrl;
        }
    }
}
