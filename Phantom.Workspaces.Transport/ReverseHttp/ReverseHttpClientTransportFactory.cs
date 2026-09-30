using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpClientTransportFactory : ITransportFactory
{
    private readonly ITransportFactory httpClientTransportFactory;
    private readonly string hubUrl;
    private readonly string entityId;
    private readonly List<string> hubUrls = [];
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan routeLeaseDuration;
    private ITransport? hubTransport;
    private IMessageChannel? registrationChannel;
    private IReachabilityRouteStore? reachabilityRouteStore;
    private EntityId? profileEntityId;
    private EntityId? hubProfileEntityId;
    private ReachabilityRouteLease? reachabilityLease;

    public ReverseHttpClientTransportFactory(string hubUrl, string entityId)
        : this(new HttpClientTransportFactory(), hubUrl, entityId)
    {
    }

    public ReverseHttpClientTransportFactory(
        string hubUrl,
        string entityId,
        ILogger<ReverseHttpClientTransportFactory>? logger)
        : this(new HttpClientTransportFactory(), hubUrl, entityId, null, null, null, logger)
    {
    }

    public ReverseHttpClientTransportFactory(HttpClientTransportFactory httpClientTransportFactory, string hubUrl, string entityId)
        : this((ITransportFactory)httpClientTransportFactory, hubUrl, entityId)
    {
    }

    public ReverseHttpClientTransportFactory(ITransportFactory httpClientTransportFactory, string hubUrl, string entityId)
        : this(httpClientTransportFactory, hubUrl, entityId, null, null, null, null)
    {
    }

    public ReverseHttpClientTransportFactory(
        ITransportFactory httpClientTransportFactory,
        string hubUrl,
        string entityId,
        IReachabilityRouteStore? reachabilityRouteStore,
        EntityId? hubProfileEntityId,
        TimeProvider? timeProvider = null,
        ILogger<ReverseHttpClientTransportFactory>? logger = null,
        TimeSpan? routeLeaseDuration = null)
    {
        this.httpClientTransportFactory = httpClientTransportFactory ?? throw new ArgumentNullException(nameof(httpClientTransportFactory));
        this.hubUrl = hubUrl ?? throw new ArgumentNullException(nameof(hubUrl));
        this.entityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        this.reachabilityRouteStore = reachabilityRouteStore;
        this.profileEntityId = reachabilityRouteStore is null ? null : new EntityId(entityId);
        this.hubProfileEntityId = hubProfileEntityId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<ReverseHttpClientTransportFactory>.Instance;
        this.routeLeaseDuration = routeLeaseDuration ?? TimeSpan.FromMinutes(2);
    }

    public string HubUrl => this.hubUrl;

    public string EntityId => this.entityId;

    public IReadOnlyList<string> HubUrls => this.hubUrls;

    public bool IsRegistered => this.registrationChannel is not null;

    public Exception? LastReachabilityPublicationError { get; private set; }

    public ReachabilityPublicationStatus? LastReachabilityPublicationStatus { get; private set; }

    public event EventHandler<ReachabilityPublicationStatus>? PublicationStatusChanged;

    public Task RetryReachabilityPublicationAsync(CancellationToken cancellationToken = default)
        => this.reachabilityLease?.PublishAsync(cancellationToken) ?? Task.CompletedTask;

    public void ConfigureReachability(
        IReachabilityRouteStore routeStore,
        EntityId profileEntityId,
        EntityId? hubProfileEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(routeStore);
        if (this.registrationChannel is not null)
        {
            throw new InvalidOperationException("Reachability must be configured before registration.");
        }

        if (!string.Equals(profileEntityId.ToString(), this.entityId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The reachability profile must match the reverse registration identity.", nameof(profileEntityId));
        }

        this.reachabilityRouteStore = routeStore;
        this.profileEntityId = profileEntityId;
        this.hubProfileEntityId = hubProfileEntityId;
    }

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

        var registered = false;
        try
        {
            var hubDescriptor = JsonSerializer.SerializeToElement(new { type = "http", url = this.hubUrl });
            this.hubTransport = await this.httpClientTransportFactory.ConnectToAsync(hubDescriptor, ct).ConfigureAwait(false)
                ?? throw new TransportException("HTTP client transport factory did not handle the hub descriptor.");
            var registerDescriptor = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["type"] = "reverse-register", ["entity-id"] = this.entityId });
            this.registrationChannel = await this.hubTransport.ConnectToMessageChannelAsync(registerDescriptor, ct)
                .ConfigureAwait(false);
            this.logger.LogInformation("Reverse registration client; outcome connected.");
            this.UpsertHubUrl();
            await this.StartReachabilityLeaseAsync(ct).ConfigureAwait(false);
            registered = true;
            return this.registrationChannel;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            this.logger.LogInformation("Reverse registration client; outcome cancelled.");
            throw;
        }
        catch (Exception error) when (error is TransportException or HttpRequestException
            or IOException or TimeoutException)
        {
            this.logger.LogWarning("Reverse registration client; outcome {Outcome}.",
                error is TimeoutException ? "timeout" : "transport-failure");
            throw new TransportException("Reverse registration could not be established.");
        }
        finally
        {
            if (!registered)
            {
                await this.DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<IMessageChannel> ReconnectAsync(CancellationToken ct = default)
    {
        this.logger.LogInformation("Reverse registration client; outcome reconnecting.");
        await this.DisconnectAsync().ConfigureAwait(false);
        return await this.EnsureRegisteredAsync(ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        var channel = this.registrationChannel;
        var transport = this.hubTransport;
        var lease = this.reachabilityLease;
        this.registrationChannel = null;
        this.hubTransport = null;
        this.reachabilityLease = null;
        this.hubUrls.Clear();

        try
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (transport is not null)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                if (this.LastReachabilityPublicationStatus is { } status)
                {
                    this.LastReachabilityPublicationStatus = status with
                    {
                        RegistrationActive = false,
                        Outcome = "disconnected",
                        Persisted = "unknown",
                    };
                    this.PublicationStatusChanged?.Invoke(this, this.LastReachabilityPublicationStatus);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            await this.httpClientTransportFactory.DisposeAsync().ConfigureAwait(false);
        }
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

    private async Task StartReachabilityLeaseAsync(CancellationToken cancellationToken)
    {
        if (this.reachabilityRouteStore is null || this.profileEntityId is null)
        {
            return;
        }

        if (this.hubProfileEntityId is not { } hubId)
        {
            return;
        }

        this.reachabilityLease = new ReachabilityRouteLease(
            this.reachabilityRouteStore,
            this.profileEntityId.Value,
            $"reverse-http:{hubId}",
            () => JsonSerializer.SerializeToElement(
                new Dictionary<string, object>
                {
                    ["type"] = "reverse-http",
                    ["hub-urls"] = new[] { this.hubUrl },
                    ["entity-id"] = this.entityId,
                }),
            priority: 100,
            this.timeProvider,
            this.routeLeaseDuration,
            this.OnPublicationStatusChanged,
            async () =>
            {
                if (this.registrationChannel is { } channel)
                    await channel.DisposeAsync().ConfigureAwait(false);
            });
        await this.reachabilityLease.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnPublicationStatusChanged(Exception? exception)
    {
        var previous = this.LastReachabilityPublicationError;
        var validation = exception as RouteValidationException;
        this.LastReachabilityPublicationError = exception is null
            ? null : (Exception?)validation ?? new InvalidOperationException("publication.unexpected");
        var status = new ReachabilityPublicationStatus(
            validation?.ReasonCode ?? (exception is null ? "none"
                : exception is UnauthorizedAccessException ? "route.owner-mismatch" : "publication.unexpected"),
            validation?.Field ?? (exception is UnauthorizedAccessException ? "route.owner-profile-entity-id" : "unknown"),
            exception?.GetType().Name ?? "none",
            this.reachabilityLease?.PublicationAttempts ?? 0,
            exception is null ? "published"
                : exception is UnauthorizedAccessException ? "terminal" : "renewal-scheduled",
            this.IsRegistered,
            exception is null ? "true"
                : validation is not null && this.reachabilityLease?.LastCleanupOutcome == "removed-or-absent"
                    ? "false" : "unknown",
            this.reachabilityLease?.LastCleanupOutcome ?? "unknown",
            validation is null ? "route-publication" : "route-validation");
        this.LastReachabilityPublicationStatus = status;
        if (exception is not null)
        {
            this.logger.LogWarning(
                "Reachability publication-failed; boundary {Boundary}; stage {Stage}; reason {Reason}; field {Field}; exception-type {ExceptionType}; attempt {Attempt}; outcome {Outcome}; registration-active {RegistrationActive}; persisted {Persisted}; cleanup {Cleanup}.",
                "route-publication", status.Stage, status.ReasonCode, status.Field, status.ExceptionType,
                status.Attempt, status.Outcome, status.RegistrationActive, status.Persisted, status.Cleanup);
        }
        else if (previous is not null)
        {
            this.logger.LogInformation(
                "Reachability publication-recovered; stage {Stage}; attempt {Attempt}; outcome {Outcome}; registration-active {RegistrationActive}; persisted {Persisted}.",
                "route-publication", status.Attempt, status.Outcome, status.RegistrationActive, status.Persisted);
        }
        this.PublicationStatusChanged?.Invoke(this, status);
    }

    public async Task ApplyHubProfileEntityIdAsync(
        string hubProfileEntityId,
        CancellationToken cancellationToken)
    {
        if (this.reachabilityRouteStore is null || this.profileEntityId is null)
        {
            return;
        }
        if (!Guid.TryParse(hubProfileEntityId, out var parsedHubProfileEntityId))
            throw new UnauthorizedAccessException("Invalid hub profile identity.");

        var hubId = new EntityId(parsedHubProfileEntityId);
        if (this.hubProfileEntityId == hubId)
        {
            return;
        }

        if (this.reachabilityLease is not null)
        {
            await this.reachabilityLease.DisposeAsync().ConfigureAwait(false);
            this.reachabilityLease = null;
        }

        this.hubProfileEntityId = hubId;
        await this.StartReachabilityLeaseAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ReachabilityPublicationStatus(
    string ReasonCode, string Field, string ExceptionType, int Attempt,
    string Outcome, bool RegistrationActive, string Persisted, string Cleanup, string Stage);
