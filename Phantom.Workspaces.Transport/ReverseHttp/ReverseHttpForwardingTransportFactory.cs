using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpForwardingTransportFactory : ITransportFactory
{
    private static readonly TimeSpan DefaultHubConnectionTimeout = TimeSpan.FromSeconds(10);
    private readonly ITransportFactory httpClientTransportFactory;
    private readonly TimeSpan hubConnectionTimeout;
    private readonly TimeSpan relayEstablishmentTimeout;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? reportStage;
    private readonly ILogger<ReverseHttpForwardingTransportFactory> logger;
    private readonly TransportPeerIdentity? authenticatedPeer;

    public ReverseHttpForwardingTransportFactory()
        : this(new HttpClientTransportFactory(), DefaultHubConnectionTimeout, null)
    {
    }

    public ReverseHttpForwardingTransportFactory(
        ITransportFactory httpClientTransportFactory,
        TimeSpan? hubConnectionTimeout = null,
        TransportPeerIdentity? authenticatedPeer = null,
        TimeSpan? relayEstablishmentTimeout = null,
        TimeProvider? timeProvider = null,
        Action<string>? reportStage = null,
        ILogger<ReverseHttpForwardingTransportFactory>? logger = null)
    {
        this.httpClientTransportFactory = httpClientTransportFactory ?? throw new ArgumentNullException(nameof(httpClientTransportFactory));
        this.hubConnectionTimeout = hubConnectionTimeout ?? DefaultHubConnectionTimeout;
        this.authenticatedPeer = authenticatedPeer;
        this.relayEstablishmentTimeout = relayEstablishmentTimeout ?? TimeSpan.FromSeconds(15);
        if (this.relayEstablishmentTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(relayEstablishmentTimeout));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.reportStage = reportStage;
        this.logger = logger ?? NullLogger<ReverseHttpForwardingTransportFactory>.Instance;
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
        this.ReportStage("hub-connection", "started");
        var pending = hubUrls.Select(url => this.ConnectToHubAsync(url, raceCancellation.Token)).ToList();
        var failures = new List<Exception>();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).WaitAsync(ct).ConfigureAwait(false);
            pending.Remove(completed);

            HubConnectionAttempt winner;
            try
            {
                winner = await completed.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                this.ReportStage("hub-connection", "failed");
                failures.Add(ex);
                continue;
            }

            // A hub connection won the race. From here a relay failure is terminal (the target machine
            // exists but rejected the relay, e.g. not-registered) and must propagate to the caller rather
            // than being retried against another hub.
            await raceCancellation.CancelAsync().ConfigureAwait(false);
            _ = DisposeLosingAttemptsAsync(pending);
            try
            {
                var relayRequestData = new Dictionary<string, object>
                {
                    ["type"] = "reverse-http",
                    ["entity-id"] = entityId,
                };
                if (SessionAttachDiagnosticScope.CurrentAttempt is { } attempt)
                    relayRequestData["diagnostic-attempt"] = attempt;
                if (this.authenticatedPeer is not null)
                {
                    relayRequestData["authenticated-peer"] = new Dictionary<string, object?>
                    {
                        ["authentication-scheme"] = this.authenticatedPeer.AuthenticationScheme,
                        ["stable-peer-id"] = this.authenticatedPeer.StablePeerId,
                        ["user-entity-id"] = this.authenticatedPeer.UserEntityId,
                        ["user-computer-profile-entity-id"] = this.authenticatedPeer.UserComputerProfileEntityId,
                    };
                }

                using var relayRequest = JsonDocument.Parse(JsonSerializer.Serialize(relayRequestData));
                using var relayTimer = new CancellationTokenSource(this.relayEstablishmentTimeout, this.timeProvider);
                using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, relayTimer.Token);
                this.ReportStage("relay-acceptance", "started");
                var opening = winner.Transport.ConnectToMessageChannelAsync(
                    relayRequest.RootElement, relayCancellation.Token);
                IMessageChannel relayChannel;
                try
                {
                    relayChannel = await opening.WaitAsync(relayCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (relayCancellation.IsCancellationRequested)
                {
                    _ = this.DisposeLateChannelAsync(opening);
                    if (!ct.IsCancellationRequested)
                        throw new TimeoutException("Reverse HTTP relay acceptance timed out.");
                    throw;
                }
                var transport = new ReverseHttpTransport(relayChannel, this.authenticatedPeer);
                try
                {
                    // Surface hub-side relay rejections (e.g. channel-open-error {"error-code":"not-registered"})
                    // as a TransportException before returning, rather than handing back a transport whose
                    // round-trips would silently hang.
                    this.ReportStage("relay-establishment", "started");
                    await transport.WaitForRelayEstablishedAsync(relayCancellation.Token).ConfigureAwait(false);
                    this.ReportStage("relay-establishment", "established");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested
                    && relayTimer.IsCancellationRequested)
                {
                    this.ReportStage("relay-establishment", "timeout");
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw new TimeoutException("Reverse HTTP relay establishment timed out.");
                }
                catch
                {
                    this.ReportStage("relay-establishment", "failed");
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                return transport;
            }
            catch
            {
                await winner.Transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw new TransportException("All reverse HTTP hub connection attempts failed.", new AggregateException(failures));
    }

    public ValueTask DisposeAsync() => this.httpClientTransportFactory.DisposeAsync();

    private void ReportStage(string stage, string outcome)
    {
        this.reportStage?.Invoke(stage);
        this.logger.LogInformation(
            "Reverse relay; attempt {Attempt}; stage {Stage}; outcome {Outcome}.",
            SessionAttachDiagnosticScope.CurrentAttempt ?? "none", stage, outcome);
    }

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
            var connecting = this.httpClientTransportFactory.ConnectToAsync(
                httpDescriptor.RootElement, timeoutCancellation.Token);
            ITransport? transport;
            try
            {
                transport = await connecting.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                _ = this.DisposeLateTransportAsync(connecting);
                throw;
            }
            if (transport is null)
                throw new TransportException("HTTP client transport factory did not handle the hub descriptor.");
            return new HubConnectionAttempt(hubUrl, transport);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to reverse HTTP hub.", ex);
        }
    }

    private async Task DisposeLateChannelAsync(Task<IMessageChannel> opening)
    {
        try
        {
            await (await opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            this.logger.LogWarning("Reverse relay; stage late-channel-cleanup; outcome failed.");
        }
    }

    private async Task DisposeLateTransportAsync(Task<ITransport?> connecting)
    {
        try
        {
            if (await connecting.ConfigureAwait(false) is { } transport)
                await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            this.logger.LogWarning("Reverse relay; stage late-hub-cleanup; outcome failed.");
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