using System.Text.Json;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpForwardingTransportFactory : ITransportFactory
{
    private static readonly TimeSpan DefaultHubConnectionTimeout = TimeSpan.FromSeconds(10);
    private readonly ITransportFactory httpClientTransportFactory;
    private readonly TimeSpan hubConnectionTimeout;

    public ReverseHttpForwardingTransportFactory()
        : this(new HttpClientTransportFactory(), DefaultHubConnectionTimeout)
    {
    }

    public ReverseHttpForwardingTransportFactory(ITransportFactory httpClientTransportFactory, TimeSpan? hubConnectionTimeout = null)
    {
        this.httpClientTransportFactory = httpClientTransportFactory ?? throw new ArgumentNullException(nameof(httpClientTransportFactory));
        this.hubConnectionTimeout = hubConnectionTimeout ?? DefaultHubConnectionTimeout;
    }

    public async Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var typeProperty)
            || !string.Equals(typeProperty.GetString(), "reverse-http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!connectionDescriptor.TryGetProperty("hub-urls", out var hubUrlsProperty)
            || hubUrlsProperty.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var hubUrls = hubUrlsProperty.EnumerateArray()
            .Select(static element => element.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        if (hubUrls.Length == 0)
        {
            return null;
        }

        if (!connectionDescriptor.TryGetProperty("entity-id", out var entityIdProperty)
            || entityIdProperty.GetString() is not { Length: > 0 } entityId)
        {
            throw new TransportException("Reverse HTTP forwarding descriptors must include entity-id.");
        }

        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = hubUrls.Select(url => this.ConnectToHubAsync(url, raceCancellation.Token)).ToList();
        var failures = new List<Exception>();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            try
            {
                var winner = await completed.ConfigureAwait(false);
                await raceCancellation.CancelAsync().ConfigureAwait(false);
                _ = DisposeLosingAttemptsAsync(pending);
                try
                {
                    using var relayRequest = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["type"] = "reverse-http",
                        ["entity-id"] = entityId,
                    }));
                    var relayChannel = await winner.Transport.ConnectToMessageChannelAsync(relayRequest.RootElement, ct).ConfigureAwait(false);
                    return new ReverseHttpTransport(relayChannel);
                }
                catch
                {
                    await winner.Transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add(ex);
            }
        }

        throw new TransportException("All reverse HTTP hub connection attempts failed.", new AggregateException(failures));
    }

    public ValueTask DisposeAsync() => this.httpClientTransportFactory.DisposeAsync();

    private async Task<HubConnectionAttempt> ConnectToHubAsync(string hubUrl, CancellationToken ct)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCancellation.CancelAfter(this.hubConnectionTimeout);
        try
        {
            using var httpDescriptor = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["type"] = "http",
                ["url"] = hubUrl,
            }));
            var transport = await this.httpClientTransportFactory.ConnectToAsync(httpDescriptor.RootElement, timeoutCancellation.Token).ConfigureAwait(false)
                ?? throw new TransportException($"HTTP client transport factory did not handle hub URL '{hubUrl}'.");
            return new HubConnectionAttempt(hubUrl, transport);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to reverse HTTP hub '{hubUrl}'.", ex);
        }
    }

    private static async Task DisposeLosingAttemptsAsync(IEnumerable<Task<HubConnectionAttempt>> attempts)
    {
        foreach (var attemptTask in attempts)
        {
            try
            {
                var attempt = await attemptTask.ConfigureAwait(false);
                await attempt.Transport.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private sealed record HubConnectionAttempt(string HubUrl, ITransport Transport);
}