using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpClientTransportFactoryTests
{
    private static readonly EntityId Worker = new("11111111-1111-4111-8111-111111111111");
    private static readonly EntityId Hub = new("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task ReverseHttpClientTransportFactory_InvalidAdvertisedHubThenCorrectedHub_PublishesAndRenewsOwnedRoute()
    {
        var clock = new FakeTimeProvider();
        var store = new ValidationStore { Reason = null };
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "http://hub.example", Worker.ToString(),
            store, Hub, clock, routeLeaseDuration: TimeSpan.FromMinutes(2),
            advertisedHubUrl: "https://hub.example/?token=private");
        var channel = await factory.EnsureRegisteredAsync();
        Assert.True(factory.IsRegistered);
        Assert.Equal("endpoint.credential-query-or-fragment", factory.LastReachabilityPublicationStatus?.ReasonCode);
        Assert.Empty(store.Routes);

        await factory.SetAdvertisedHubUrlAsync("http://hub.example", CancellationToken.None);
        Assert.Same(channel, await factory.EnsureRegisteredAsync());
        Assert.Equal("http://hub.example/", store.Routes.Single().Descriptor.GetProperty("hub-urls")[0].GetString());
        Assert.Equal("true", factory.LastReachabilityPublicationStatus?.Persisted);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.PublicationStatusChanged += (_, status) =>
        {
            if (status.Persisted == "true" && status.Attempt >= 3)
                renewed.TrySetResult();
        };
        clock.Advance(TimeSpan.FromMinutes(1));
        await renewed.Task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Single(store.Routes);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_RepeatedInvalidAdvertisement_ReportsTransitionWithoutWarningSpam()
    {
        using var logs = new CapturingLoggerFactory();
        var store = new ValidationStore { Reason = null };
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "http://hub.example", Worker.ToString(),
            store, Hub, logger: logs.CreateLogger<ReverseHttpClientTransportFactory>(),
            advertisedHubUrl: "https://hub.example/?token=private");
        await factory.EnsureRegisteredAsync();
        await factory.RetryReachabilityPublicationAsync();
        Assert.True(factory.IsRegistered);
        Assert.Equal(1, logs.Entries.Count(entry => entry.Message.Contains("publication-failed")));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("network protection"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("verify TLS"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("publication-recovered"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("hub.example"));
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_RegistrationInfoRouteValidationFails_KeepsRegistrationAndReportsPublicationError()
    {
        var store = new ValidationStore();
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "https://user:private@private-hub.example/",
            Worker.ToString(), store, null);
        var channel = await factory.EnsureRegisteredAsync();
        await factory.ApplyHubProfileEntityIdAsync(Hub.ToString(), CancellationToken.None);
        Assert.Same(channel, await factory.EnsureRegisteredAsync());
        Assert.True(factory.IsRegistered);
        Assert.Equal("endpoint.userinfo", factory.LastReachabilityPublicationStatus?.ReasonCode);
        Assert.Equal("route-validation", factory.LastReachabilityPublicationStatus?.Stage);
        Assert.Equal("false", factory.LastReachabilityPublicationStatus?.Persisted);
        Assert.Equal("removed-or-absent", factory.LastReachabilityPublicationStatus?.Cleanup);
        Assert.Empty(store.Routes);
        await factory.DisconnectAsync();
        Assert.False(factory.LastReachabilityPublicationStatus?.RegistrationActive);
        Assert.Equal("disconnected", factory.LastReachabilityPublicationStatus?.Outcome);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_InvalidInitialRouteThenValidRoute_RecoversPublicationWithoutGhostRegistration()
    {
        var store = new ValidationStore();
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "https://hub.example/", Worker.ToString(), store, Hub);
        var channel = await factory.EnsureRegisteredAsync();
        Assert.Same(channel, await factory.EnsureRegisteredAsync());
        Assert.NotNull(factory.LastReachabilityPublicationError);
        store.Reason = null;
        await factory.RetryReachabilityPublicationAsync();
        Assert.Null(factory.LastReachabilityPublicationError);
        Assert.Equal("true", factory.LastReachabilityPublicationStatus?.Persisted);
        Assert.Single(store.Routes);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_ValidationCleanupFailure_ReportsUnknownPersistence()
    {
        var store = new ValidationStore { FailRemoval = true };
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "https://hub.example/", Worker.ToString(), store, Hub);
        await factory.EnsureRegisteredAsync();
        Assert.True(factory.IsRegistered);
        Assert.Equal("unknown", factory.LastReachabilityPublicationStatus?.Persisted);
        Assert.Equal("unknown", factory.LastReachabilityPublicationStatus?.Cleanup);
        store.FailRemoval = false;
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_TargetIdentityMismatch_FailsClosed()
    {
        var store = new ValidationStore { Reason = "descriptor.entity-id.target-mismatch" };
        var http = new FakeHttpTransportFactory();
        await using var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example/", Worker.ToString(), store, Hub);
        var failure = await Assert.ThrowsAsync<RouteValidationException>(() => factory.EnsureRegisteredAsync());
        Assert.True(failure.IsIdentityFailure);
        Assert.False(factory.IsRegistered);
        Assert.True(http.Transports.Single().Channels.Single().Disposed);
        Assert.Empty(store.Routes);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_RenewalTargetIdentityMismatch_ClosesRegistration()
    {
        var clock = new FakeTimeProvider();
        var store = new ValidationStore { Reason = null };
        var http = new FakeHttpTransportFactory();
        await using var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example/", Worker.ToString(), store, Hub,
            clock, routeLeaseDuration: TimeSpan.FromMinutes(2));
        await factory.EnsureRegisteredAsync();
        await store.Attempts.Reader.ReadAsync();
        store.Reason = "descriptor.entity-id.target-mismatch";
        clock.Advance(TimeSpan.FromMinutes(1));
        await http.Transports.Single().Channels.Single().Reader.Completion.WaitAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal("route-identity", factory.LastReachabilityPublicationStatus?.Stage);
        Assert.Equal("terminal", factory.LastReachabilityPublicationStatus?.Outcome);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_DistinctRouteRejections_LogSafeStagesAndRecover()
    {
        using var logs = new CapturingLoggerFactory();
        var store = new ValidationStore();
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "https://private-hub.example/?token=private",
            Worker.ToString(), store, Hub, logger: logs.CreateLogger<ReverseHttpClientTransportFactory>());
        await factory.EnsureRegisteredAsync();
        store.Reason = "route-id.invalid";
        await factory.SetAdvertisedHubUrlAsync("https://hub.example/", CancellationToken.None);
        store.Reason = null;
        await factory.RetryReachabilityPublicationAsync();
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("endpoint.credential-query-or-fragment")
            && entry.Message.Contains("descriptor.hub-urls[]") && entry.Message.Contains("route-publication")
            && entry.Message.Contains("route-validation") && entry.Message.Contains("persisted false"));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("route-id.invalid") && entry.Message.Contains("attempt 2"));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("publication-recovered"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Exception is not null
            || entry.Message.Contains("private-hub", StringComparison.Ordinal)
            || entry.Message.Contains("token=private", StringComparison.Ordinal)
            || entry.Message.Contains(Worker.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_RenewalValidationFailure_RemovesStaleSlotAndRecovers()
    {
        var clock = new FakeTimeProvider();
        var store = new ValidationStore { Reason = null };
        await using var factory = new ReverseHttpClientTransportFactory(
            new FakeHttpTransportFactory(), "https://hub.example/", Worker.ToString(), store, Hub,
            clock, routeLeaseDuration: TimeSpan.FromMinutes(2));
        await factory.EnsureRegisteredAsync();
        await store.Attempts.Reader.ReadAsync();
        Assert.Single(store.Routes);
        store.Reason = "endpoint.invalid-scheme";
        var rejected = new TaskCompletionSource<ReachabilityPublicationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.PublicationStatusChanged += (_, status) =>
        {
            if (status.ReasonCode == "endpoint.invalid-scheme")
                rejected.TrySetResult(status);
        };
        clock.Advance(TimeSpan.FromMinutes(1));
        await rejected.Task.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Empty(store.Routes);
        Assert.True(factory.IsRegistered);
        Assert.Equal("endpoint.invalid-scheme", factory.LastReachabilityPublicationStatus?.ReasonCode);
        store.Reason = null;
        await factory.RetryReachabilityPublicationAsync();
        Assert.Single(store.Routes);
        Assert.Null(factory.LastReachabilityPublicationError);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_RenewalAuthorizationFailure_ClosesRegistration()
    {
        var clock = new FakeTimeProvider();
        var store = new ValidationStore { Reason = null };
        var http = new FakeHttpTransportFactory();
        await using var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example/", Worker.ToString(), store, Hub,
            clock, routeLeaseDuration: TimeSpan.FromMinutes(2));
        await factory.EnsureRegisteredAsync();
        await store.Attempts.Reader.ReadAsync();
        store.Deny = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await http.Transports.Single().Channels.Single().Reader.Completion.WaitAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal("route.owner-mismatch", factory.LastReachabilityPublicationStatus?.ReasonCode);
        Assert.Equal("terminal", factory.LastReachabilityPublicationStatus?.Outcome);
    }

    [Fact]
    public void ReverseHttpClientTransportFactory_Startup_ClearsHubUrls()
    {
        var factory = new ReverseHttpClientTransportFactory(new FakeHttpTransportFactory(), "https://hub.example", "machine-c");

        Assert.Empty(factory.HubUrls);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_Connect_UpsertsHubUrl()
    {
        var httpFactory = new FakeHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(httpFactory, "https://hub.example", "machine-c");

        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        var transport = await factory.ConnectToAsync(descriptor.RootElement);

        Assert.NotNull(transport);
        Assert.Equal(["https://hub.example"], factory.HubUrls);
        Assert.Equal("""{"type":"http","url":"https://hub.example"}""", httpFactory.ConnectionDescriptors.Single().GetRawText());
        Assert.Equal("reverse-register", httpFactory.Transports.Single().ChannelRequests.Single().GetProperty("type").GetString());
        Assert.Equal("machine-c", httpFactory.Transports.Single().ChannelRequests.Single().GetProperty("entity-id").GetString());
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_Reconnect_ReplacesHubUrl()
    {
        var httpFactory = new FakeHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(httpFactory, "https://hub.example", "machine-c");
        await factory.EnsureRegisteredAsync();

        await factory.ReconnectAsync();

        Assert.Equal(["https://hub.example"], factory.HubUrls);
        Assert.Equal(2, httpFactory.ConnectionDescriptors.Count);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_Disconnect_RemovesHubUrl()
    {
        var factory = new ReverseHttpClientTransportFactory(new FakeHttpTransportFactory(), "https://hub.example", "machine-c");
        await factory.EnsureRegisteredAsync();

        await factory.DisposeAsync();

        Assert.Empty(factory.HubUrls);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_InitialRegistrationFailsAfterChannelOpen_FencesChannelAndRoute()
    {
        var http = new FakeHttpTransportFactory();
        var store = new FailingOnceRouteStore();
        var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example", "11111111-1111-4111-8111-111111111111",
            store, new EntityId("22222222-2222-4222-8222-222222222222"));
        await using (factory)
        {
            await Assert.ThrowsAsync<TransportException>(() => factory.EnsureRegisteredAsync());

            Assert.False(factory.IsRegistered);
            Assert.Empty(factory.HubUrls);
            Assert.True(http.Transports.Single().Disposed);
            Assert.True(http.Transports.Single().Channels.Single().Disposed);
            Assert.Equal(1, store.Removals);

            var fresh = await factory.EnsureRegisteredAsync();
            Assert.NotSame(http.Transports[0].Channels.Single(), fresh);
            Assert.True(factory.IsRegistered);
            Assert.Equal(["https://hub.example"], factory.HubUrls);
        }
    }

    [Fact]
    public void ReverseHttpClientTransportFactory_AutoReconnect_ExponentialBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(2), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(4), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(60), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(10));
        Assert.Equal(TimeSpan.FromSeconds(30), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(10, jitterFactor: 0.5));
    }

    internal sealed class FakeHttpTransportFactory : ITransportFactory
    {
        public List<JsonElement> ConnectionDescriptors { get; } = [];

        public List<FakeTransport> Transports { get; } = [];

        public bool Disposed { get; private set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.ConnectionDescriptors.Add(connectionDescriptor.Clone());
            var transport = new FakeTransport();
            this.Transports.Add(transport);
            return Task.FromResult<ITransport?>(transport);
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeTransport : ITransport
    {
        public List<JsonElement> ChannelRequests { get; } = [];

        public List<FakeMessageChannel> Channels { get; } = [];

        public bool Disposed { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.ChannelRequests.Add(request.Clone());
            var channel = new FakeMessageChannel();
            this.Channels.Add(channel);
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();

        public bool Disposed { get; private set; }

        public ChannelWriter<JsonElement> Writer => this.channel.Writer;

        public ChannelReader<JsonElement> Reader => this.channel.Reader;

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingOnceRouteStore : IReachabilityRouteStore
    {
        private bool fail = true;

        public int Removals { get; private set; }

        public Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
            EntityId profileEntityId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReachabilityRoute>>([]);

        public Task UpsertRouteAsync(
            EntityId profileEntityId, ReachabilityRoute route, CancellationToken cancellationToken = default)
        {
            if (this.fail)
            {
                this.fail = false;
                throw new IOException("Unanticipated route store failure.");
            }

            return Task.CompletedTask;
        }

        public Task RemoveRouteAsync(
            EntityId profileEntityId, string routeId, EntityId ownerProfileEntityId,
            CancellationToken cancellationToken = default)
        {
            this.Removals++;
            return Task.CompletedTask;
        }

        public Task ClearOwnedRoutesAsync(
            EntityId profileEntityId, EntityId ownerProfileEntityId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ValidationStore : IReachabilityRouteStore
    {
        public string? Reason { get; set; } = "endpoint.userinfo";
        public bool Deny { get; set; }
        public bool FailRemoval { get; set; }
        public Channel<int> Attempts { get; } = Channel.CreateUnbounded<int>();
        public List<ReachabilityRoute> Routes { get; } = [];
        public Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(EntityId profileEntityId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReachabilityRoute>>(this.Routes);
        public Task UpsertRouteAsync(EntityId profileEntityId, ReachabilityRoute route, CancellationToken cancellationToken = default)
        {
            this.Attempts.Writer.TryWrite(1);
            if (this.Deny)
                throw new UnauthorizedAccessException("private identity");
            if (this.Reason is { } reason)
                throw new RouteValidationException(reason, reason.StartsWith("endpoint.", StringComparison.Ordinal)
                    ? "descriptor.hub-urls[]" : "route-id");
            this.Routes.Clear();
            this.Routes.Add(route);
            return Task.CompletedTask;
        }
        public Task RemoveRouteAsync(EntityId profileEntityId, string routeId, EntityId ownerProfileEntityId, CancellationToken cancellationToken = default)
        {
            if (this.FailRemoval)
                throw new ReachabilityRouteStoreException("private route");
            this.Routes.RemoveAll(route => route.RouteId == routeId && route.OwnerProfileEntityId == ownerProfileEntityId);
            return Task.CompletedTask;
        }
        public Task ClearOwnedRoutesAsync(EntityId profileEntityId, EntityId ownerProfileEntityId, CancellationToken cancellationToken = default)
        {
            this.Routes.RemoveAll(route => route.OwnerProfileEntityId == ownerProfileEntityId);
            return Task.CompletedTask;
        }
    }
}
