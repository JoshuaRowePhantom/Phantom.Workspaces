using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport;

public sealed class UserComputerProfileTransportFactory : ITransportFactory
{
    private const string DescriptorType = "user-computer-profile";
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly WorkspaceEntitySession workspaceEntitySession;
    private readonly ITransportFactoryRegistry transportFactoryRegistry;
    private readonly IReadOnlyCollection<string> reverseHttpHubUrls;
    private readonly ReverseHttpServerTransportFactory? liveInboundRegistry;
    private readonly IReachabilityRouteStore reachabilityRouteStore;
    private readonly TimeProvider timeProvider;
    private readonly bool trustedTransientRoutesEnabled;
    private readonly TransportPeerIdentity localPeer;
    private readonly ILogger<UserComputerProfileTransportFactory> logger;

    public UserComputerProfileTransportFactory(
        IDataAccessLayer dataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession,
        ITransportFactoryRegistry transportFactoryRegistry,
        IReadOnlyCollection<string>? reverseHttpHubUrls = null,
        ReverseHttpServerTransportFactory? liveInboundRegistry = null,
        IReachabilityRouteStore? reachabilityRouteStore = null,
        TimeProvider? timeProvider = null,
        bool trustedTransientRoutesEnabled = false,
        ILogger<UserComputerProfileTransportFactory>? logger = null)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.workspaceEntitySession = workspaceEntitySession ?? throw new ArgumentNullException(nameof(workspaceEntitySession));
        this.transportFactoryRegistry = transportFactoryRegistry ?? throw new ArgumentNullException(nameof(transportFactoryRegistry));
        this.reverseHttpHubUrls = reverseHttpHubUrls ?? [];
        this.liveInboundRegistry = liveInboundRegistry;
        this.reachabilityRouteStore =
            reachabilityRouteStore ?? new DataAccessReachabilityRouteStore(dataAccessLayer);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.trustedTransientRoutesEnabled = trustedTransientRoutesEnabled;
        this.logger = logger ?? NullLogger<UserComputerProfileTransportFactory>.Instance;
        this.localPeer = new TransportPeerIdentity
        {
            AuthenticationScheme = "local-workspace-session",
            StablePeerId = workspaceEntitySession.UserComputerProfileEntityId.ToString(),
            UserEntityId = workspaceEntitySession.UserEntityId.ToString(),
            UserComputerProfileEntityId = workspaceEntitySession.UserComputerProfileEntityId.ToString(),
        };
    }

    public async Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), DescriptorType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!connectionDescriptor.TryGetProperty("entity-id", out var entityIdProperty)
            || entityIdProperty.GetString() is not { Length: > 0 } entityIdText)
        {
            throw new TransportException("User computer profile descriptors must include entity-id.");
        }

        var entityId = new EntityId(entityIdText);
        _ = await this.GetRequiredProfileEntityAsync(entityId, ct).ConfigureAwait(false);
        if (entityId == this.workspaceEntitySession.UserComputerProfileEntityId)
        {
            this.ReportRoute("local", "selected");
            using var localDocument = JsonDocument.Parse("""{"type":"local"}""");
            return await this.ConnectAndTargetAsync(
                localDocument.RootElement,
                connectionDescriptor,
                ct).ConfigureAwait(false);
        }

        if (this.liveInboundRegistry is not null)
        {
            ITransport? live;
            try
            {
                live = await this.liveInboundRegistry.ConnectToRegisteredAsync(
                    entityId.ToString(),
                    this.localPeer,
                    ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                live = null;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                live = null;
            }

            if (live is not null)
            {
                this.ReportRoute("live", "selected");
                return WrapTarget(live, connectionDescriptor);
            }
            this.ReportRoute("live", "unavailable");
        }

        var failures = new List<Exception>();
        if (this.trustedTransientRoutesEnabled
            && connectionDescriptor.TryGetProperty("force-route", out var forceRoute)
            && forceRoute.ValueKind == JsonValueKind.Object)
        {
            var normalizedForceRoute = NormalizeTransientRoute(forceRoute);
            var forced = await this.TryConnectAsync(normalizedForceRoute, failures, ct).ConfigureAwait(false);
            if (forced is not null)
            {
                this.ReportRoute("transient", "selected");
                return WrapTarget(forced, connectionDescriptor);
            }
        }

        var persistedRoutes = await this.reachabilityRouteStore.GetRoutesAsync(entityId, ct).ConfigureAwait(false);
        foreach (var route in ReadUsableRoutes(persistedRoutes, entityId, this.timeProvider.GetUtcNow()))
        {
            var transport = await this.TryConnectAsync(route.Descriptor, failures, ct).ConfigureAwait(false);
            if (transport is not null)
            {
                this.ReportRoute("persisted", "selected");
                return WrapTarget(transport, connectionDescriptor);
            }
        }

        if (this.trustedTransientRoutesEnabled
            && connectionDescriptor.TryGetProperty("connection-descriptor", out var legacyRoute)
            && legacyRoute.ValueKind == JsonValueKind.Object)
        {
            var normalizedLegacyRoute = NormalizeTransientRoute(legacyRoute);
            var legacy = await this.TryConnectAsync(normalizedLegacyRoute, failures, ct).ConfigureAwait(false);
            if (legacy is not null)
            {
                this.ReportRoute("transient", "selected");
                return WrapTarget(legacy, connectionDescriptor);
            }
        }

        if (this.reverseHttpHubUrls.Count > 0)
        {
            var configuredRoute = JsonSerializer.SerializeToElement(
                new Dictionary<string, object>
                {
                    ["type"] = "reverse-http",
                    ["hub-urls"] = this.reverseHttpHubUrls,
                    ["entity-id"] = entityId.ToString(),
                });
            var configured = await this.TryConnectAsync(configuredRoute, failures, ct).ConfigureAwait(false);
            if (configured is not null)
            {
                this.ReportRoute("configured", "selected");
                return WrapTarget(configured, connectionDescriptor);
            }
        }

        if (failures.Count > 0)
        {
            this.ReportRoute("none", "all-failed");
            throw new TransportException(
                $"All routes to remote user computer profile '{entityId}' failed.",
                new AggregateException(failures));
        }

        this.ReportRoute("none", "missing");
        throw new TransportException(
            $"Remote user computer profile descriptor '{entityId}' has no transient route and no configured reverse HTTP hub.");
    }

    private void ReportRoute(string route, string outcome)
        => this.logger.LogInformation(
            "Remote profile transport; attempt {Attempt}; stage transport-choice; route {Route}; outcome {Outcome}.",
            SessionAttachDiagnosticScope.CurrentAttempt ?? "none", route, outcome);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<ITransport?> TryConnectAsync(
        JsonElement route,
        ICollection<Exception> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            return await this.transportFactoryRegistry.ConnectToAsync(route, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TransportException exception)
        {
            failures.Add(exception);
            return null;
        }
        catch (HttpRequestException exception)
        {
            failures.Add(exception);
            return null;
        }
        catch (IOException exception)
        {
            failures.Add(exception);
            return null;
        }
        catch (TimeoutException exception)
        {
            failures.Add(exception);
            return null;
        }
    }

    private static JsonElement NormalizeTransientRoute(JsonElement descriptor)
    {
        if (!descriptor.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { } type)
        {
            throw new TransportException("Transient route descriptors must include type.");
        }

        if (string.Equals(type, "http", StringComparison.Ordinal))
        {
            if (descriptor.EnumerateObject().Any(
                    property => property.Name is not ("type" or "url"))
                || !descriptor.TryGetProperty("url", out var urlProperty)
                || urlProperty.GetString() is not { Length: > 0 } url)
            {
                throw new TransportException("Transient HTTP routes must contain exactly type and url.");
            }

            try
            {
                return JsonSerializer.SerializeToElement(
                    new Dictionary<string, object>
                    {
                        ["type"] = "http",
                        ["url"] = DataAccessReachabilityRouteStore.NormalizeEndpoint(url),
                    });
            }
            catch (ArgumentException exception)
            {
                throw new TransportException("Transient HTTP route URL is not allowed.", exception);
            }
        }

        if (string.Equals(type, "reverse-http", StringComparison.Ordinal))
        {
            if (descriptor.EnumerateObject().Any(
                    property => property.Name is not ("type" or "hub-urls" or "entity-id"))
                || !descriptor.TryGetProperty("hub-urls", out var hubUrls)
                || hubUrls.ValueKind != JsonValueKind.Array
                || !descriptor.TryGetProperty("entity-id", out var entityId)
                || !Guid.TryParse(entityId.GetString(), out _))
            {
                throw new TransportException(
                    "Transient reverse HTTP routes must contain exactly type, hub-urls, and a valid entity-id.");
            }

            try
            {
                var urls = hubUrls.EnumerateArray()
                    .Select(static value => value.GetString())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => DataAccessReachabilityRouteStore.NormalizeEndpoint(value!))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (urls.Length is < 1 or > 8)
                {
                    throw new TransportException("Transient reverse HTTP routes require between one and eight unique hub URLs.");
                }

                return JsonSerializer.SerializeToElement(
                    new Dictionary<string, object>
                    {
                        ["type"] = "reverse-http",
                        ["hub-urls"] = urls,
                        ["entity-id"] = entityId.GetString()!,
                    });
            }
            catch (ArgumentException exception)
            {
                throw new TransportException("Transient reverse HTTP route URL is not allowed.", exception);
            }
        }

        throw new TransportException($"Transient route type '{type}' is not supported.");
    }

    private static IReadOnlyList<ReachabilityRoute> ReadUsableRoutes(
        IReadOnlyList<ReachabilityRoute> routes,
        EntityId targetProfileEntityId,
        DateTimeOffset now)
    {
        var result = new List<ReachabilityRoute>();
        foreach (var route in routes)
        {
            if (route.OwnerProfileEntityId != targetProfileEntityId
                || route.ExpiresAt <= now
                || route.ExpiresAt <= route.LastConfirmed)
            {
                continue;
            }

            JsonElement descriptor;
            try
            {
                descriptor = NormalizeTransientRoute(route.Descriptor);
            }
            catch (TransportException)
            {
                continue;
            }

            var type = descriptor.GetProperty("type").GetString();
            if ((route.RouteId == "direct-http" && type != "http")
                || (route.RouteId.StartsWith("reverse-http:", StringComparison.Ordinal)
                    && (type != "reverse-http"
                        || !Guid.TryParse(route.RouteId["reverse-http:".Length..], out _)
                        || !string.Equals(
                            descriptor.GetProperty("entity-id").GetString(),
                            targetProfileEntityId.ToString(),
                            StringComparison.OrdinalIgnoreCase)))
                || (route.RouteId != "direct-http"
                    && !route.RouteId.StartsWith("reverse-http:", StringComparison.Ordinal)))
            {
                continue;
            }

            result.Add(route with { Descriptor = descriptor });
        }

        return result
            .OrderBy(static route => route.Priority)
            .ThenBy(static route => GetTypeRank(route.Descriptor))
            .ThenBy(static route => route.RouteId, StringComparer.Ordinal)
            .ToArray();
    }

    private static int GetTypeRank(JsonElement descriptor)
        => descriptor.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "http", StringComparison.Ordinal)
                ? 0
                : 1;

    private async Task<JsonElement> GetRequiredProfileEntityAsync(EntityId entityId, CancellationToken ct)
    {
        var result = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            },
            ct).ConfigureAwait(false);
        var entity = result.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId);
        if (entity?.Data is not { } data)
        {
            throw new TransportException($"User computer profile entity '{entityId}' could not be resolved.");
        }

        return data.Clone();
    }

    private async Task<ITransport> ConnectAndTargetAsync(
        JsonElement route,
        JsonElement connectionDescriptor,
        CancellationToken cancellationToken)
    {
        var transport = await this.transportFactoryRegistry.ConnectToAsync(route, cancellationToken).ConfigureAwait(false);
        return WrapTarget(transport, connectionDescriptor);
    }

    private static ITransport WrapTarget(ITransport transport, JsonElement connectionDescriptor)
    {
        if (connectionDescriptor.TryGetProperty("target", out var target)
            && target.ValueKind == JsonValueKind.Object)
        {
            return new TargetedTransport(transport, target.Clone());
        }

        return transport;
    }

    private sealed class TargetedTransport : ITransport
    {
        private readonly ITransport inner;
        private readonly JsonElement targetDescriptor;

        public TargetedTransport(ITransport inner, JsonElement targetDescriptor)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.targetDescriptor = targetDescriptor.Clone();
        }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => this.inner.ConnectToMessageChannelAsync(this.targetDescriptor, ct);

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => this.inner.ConnectToStreamAsync(this.targetDescriptor, ct);

        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }
}
