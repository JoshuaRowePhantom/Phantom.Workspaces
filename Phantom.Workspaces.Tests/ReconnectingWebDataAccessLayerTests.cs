using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ReconnectingWebDataAccessLayerTests
{
    private static readonly DevTunnelReconnectOptions NoJitterOptions =
        new(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(8), MaxAttempts: null, JitterFraction: 0.0);

    private static DevTunnelEndpointResolution Endpoint(string uri) => new(new Uri(uri), TunnelAuthToken: null);

    [Fact]
    public async Task Operation_WhenHealthy_ForwardsToInnerLayer_WithSingleResolution()
    {
        var resolveCount = 0;
        var layer = new FakeLayer();
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => { resolveCount++; return Task.FromResult(Endpoint("https://t-5280.usw2.devtunnels.ms/")); },
            buildDataAccessLayer: _ => layer,
            delayScheduler: new RecordingDelayScheduler(),
            reconnectOptions: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);
        var result = await reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(DevTunnelConnectionState.Connected, reconnecting.Status.State);
        Assert.Equal(1, resolveCount);
        Assert.Equal(1, layer.UpdateCallCount);
    }

    [Fact]
    public async Task Operation_OnConnectivityFailure_ReResolvesReconnectsAndRetries()
    {
        var endpoints = new Queue<DevTunnelEndpointResolution>(
        [
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
            Endpoint("https://t-6000.usw2.devtunnels.ms/"),
        ]);
        // Layer 1 (initial) fails the update once with a connectivity error; layer 2 (after reconnect) succeeds.
        var layers = new Queue<FakeLayer>(
        [
            new FakeLayer { UpdateBehavior = () => throw ConnectivityFailure() },
            new FakeLayer(),
        ]);
        var scheduler = new RecordingDelayScheduler();
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => Task.FromResult(endpoints.Dequeue()),
            buildDataAccessLayer: _ => layers.Dequeue(),
            delayScheduler: scheduler,
            reconnectOptions: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);
        var result = await reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(DevTunnelConnectionState.Connected, reconnecting.Status.State);
        Assert.Equal(new Uri("https://t-6000.usw2.devtunnels.ms/"), reconnecting.Status.CurrentBaseUri);
        Assert.Equal([TimeSpan.FromSeconds(1)], scheduler.RecordedDelays);
    }

    [Fact]
    public async Task Operation_On401_TriggersReconnectAndRetries()
    {
        var endpoints = new Queue<DevTunnelEndpointResolution>(
        [
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
        ]);
        // Layer 1 (initial) fails the update with 401; layer 2 (after reconnect) succeeds.
        var layers = new Queue<FakeLayer>(
        [
            new FakeLayer { UpdateBehavior = () => throw UnauthorizedFailure() },
            new FakeLayer(),
        ]);
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => Task.FromResult(endpoints.Dequeue()),
            buildDataAccessLayer: _ => layers.Dequeue(),
            delayScheduler: new RecordingDelayScheduler(),
            reconnectOptions: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);
        var result = await reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(DevTunnelConnectionState.Connected, reconnecting.Status.State);
    }

    [Fact]
    public async Task Operation_OnApplicationError_DoesNotReconnect_AndPropagates()
    {
        var resolveCount = 0;
        var layer = new FakeLayer { UpdateBehavior = () => throw ApplicationError() };
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => { resolveCount++; return Task.FromResult(Endpoint("https://t-5280.usw2.devtunnels.ms/")); },
            buildDataAccessLayer: _ => layer,
            delayScheduler: new RecordingDelayScheduler(),
            reconnectOptions: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<WebDataAccessRequestException>(() => reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(1, resolveCount); // only the initial connect; no reconnection attempted
        Assert.Equal(DevTunnelConnectionState.Connected, reconnecting.Status.State);
    }

    [Fact]
    public async Task Operation_WhenReconnectExhausted_ReportsFailedAndThrows()
    {
        var buildCount = 0;
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => Task.FromResult(Endpoint("https://t-5280.usw2.devtunnels.ms/")),
            buildDataAccessLayer: _ =>
            {
                buildCount++;
                // First built layer (initial connect) is healthy for validation but fails the update;
                // every layer built during reconnect fails its validation probe (GetAsync).
                return buildCount == 1
                    ? new FakeLayer { UpdateBehavior = () => throw ConnectivityFailure() }
                    : new FakeLayer { GetBehavior = () => throw ConnectivityFailure() };
            },
            delayScheduler: new RecordingDelayScheduler(),
            reconnectOptions: NoJitterOptions with { MaxAttempts = 2 },
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<WebDataAccessRequestException>(() => reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(DevTunnelConnectionState.Failed, reconnecting.Status.State);
    }

    [Fact]
    public async Task ReconnectingWebDataAccessLayer_When404_ClassifiesAsTransientDisconnect()
    {
        var endpoints = new Queue<DevTunnelEndpointResolution>(
        [
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
        ]);
        // Layer 1 (initial) fails the update with 404; layer 2 (after reconnect) succeeds.
        var layers = new Queue<FakeLayer>(
        [
            new FakeLayer { UpdateBehavior = () => throw NotFoundFailure() },
            new FakeLayer(),
        ]);
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => Task.FromResult(endpoints.Dequeue()),
            buildDataAccessLayer: _ => layers.Dequeue(),
            delayScheduler: new RecordingDelayScheduler(),
            reconnectOptions: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);
        var result = await reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(DevTunnelConnectionState.Connected, reconnecting.Status.State);
    }

    [Fact]
    public async Task ReconnectingWebDataAccessLayer_When503_ClassifiesAsTransientDisconnect()
    {
        var endpoints = new Queue<DevTunnelEndpointResolution>(
        [
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
            Endpoint("https://t-5280.usw2.devtunnels.ms/"),
        ]);
        // Layer 1 (initial) fails the update with 503; layer 2 (after reconnect) succeeds.
        var layers = new Queue<FakeLayer>(
        [
            new FakeLayer { UpdateBehavior = () => throw ServiceUnavailableFailure() },
            new FakeLayer(),
        ]);
        var reconnecting = new ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: _ => Task.FromResult(endpoints.Dequeue()),
            buildDataAccessLayer: _ => layers.Dequeue(),
            delayScheduler: new RecordingDelayScheduler(),
            reconnectOptions: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await reconnecting.StartAsync(TestContext.Current.CancellationToken);
        var result = await reconnecting.UpdateAsync(EmptyUpdateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(DevTunnelConnectionState.Connected, reconnecting.Status.State);
    }

    private static WebDataAccessRequestException ConnectivityFailure()
        => new("relay unreachable", statusCode: null);

    private static WebDataAccessRequestException UnauthorizedFailure()
        => new("unauthorized", HttpStatusCode.Unauthorized);

    private static WebDataAccessRequestException NotFoundFailure()
        => new("not found", HttpStatusCode.NotFound);

    private static WebDataAccessRequestException ServiceUnavailableFailure()
        => new("service unavailable", HttpStatusCode.ServiceUnavailable);

    private static WebDataAccessRequestException ApplicationError()
        => new("bad request", HttpStatusCode.BadRequest);

    private static UpdateRequest EmptyUpdateRequest() => new()
    {
        UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
        Changes = [],
    };

    private sealed class RecordingDelayScheduler : IDelayScheduler
    {
        public List<TimeSpan> RecordedDelays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            this.RecordedDelays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLayer : IDataAccessLayer
    {
        public Func<UpdateResult>? UpdateBehavior { get; init; }

        public Func<GetResult>? GetBehavior { get; init; }

        public int UpdateCallCount { get; private set; }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            this.UpdateCallCount++;
            return Task.FromResult(this.UpdateBehavior?.Invoke() ?? new UpdateResult { EntityResults = [] });
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(this.GetBehavior?.Invoke() ?? new GetResult { Batches = [] });

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

#pragma warning disable CS0618
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
