using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelConnectionMonitorTests
{
    private static readonly DevTunnelReconnectOptions NoJitterOptions =
        new(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(8), MaxAttempts: null, JitterFraction: 0.0);

    [Fact]
    public async Task StartAsync_ResolvesAndConnects_ReportsConnected()
    {
        var resolution = new DevTunnelEndpointResolution(new Uri("https://t-5280.usw2.devtunnels.ms/"), null);
        var resolveCount = 0;
        var monitor = new DevTunnelConnectionMonitor(
            resolveEndpoint: _ => { resolveCount++; return Task.FromResult(resolution); },
            connect: (_, _) => Task.CompletedTask,
            delayScheduler: new RecordingDelayScheduler(),
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await monitor.StartAsync();

        Assert.Equal(DevTunnelConnectionState.Connected, monitor.Status.State);
        Assert.Equal(resolution.BaseUri, monitor.Status.CurrentBaseUri);
        Assert.Equal(1, resolveCount);
    }

    [Fact]
    public async Task HandleConnectionFailedAsync_ReResolvesAndReconnects_PickingUpChangedPort()
    {
        var endpoints = new Queue<DevTunnelEndpointResolution>(
        [
            new DevTunnelEndpointResolution(new Uri("https://t-5280.usw2.devtunnels.ms/"), null),
            new DevTunnelEndpointResolution(new Uri("https://t-6000.usw2.devtunnels.ms/"), null),
        ]);
        var connectAttempts = 0;
        var monitor = new DevTunnelConnectionMonitor(
            resolveEndpoint: _ => Task.FromResult(endpoints.Dequeue()),
            connect: (_, _) =>
            {
                connectAttempts++;
                return connectAttempts == 1 ? throw new InvalidOperationException("relay drop") : Task.CompletedTask;
            },
            delayScheduler: new RecordingDelayScheduler(),
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await monitor.HandleConnectionFailedAsync(new InvalidOperationException("initial drop"));

        Assert.Equal(DevTunnelConnectionState.Connected, monitor.Status.State);
        Assert.Equal(new Uri("https://t-6000.usw2.devtunnels.ms/"), monitor.Status.CurrentBaseUri);
    }

    [Fact]
    public async Task HandleConnectionFailedAsync_FollowsBoundedExponentialBackoff()
    {
        var scheduler = new RecordingDelayScheduler();
        var connectAttempts = 0;
        var monitor = new DevTunnelConnectionMonitor(
            resolveEndpoint: _ => Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://t-5280.usw2.devtunnels.ms/"), null)),
            connect: (_, _) =>
            {
                connectAttempts++;
                // Fail the first 4 retries, succeed on the 5th.
                return connectAttempts < 5 ? throw new InvalidOperationException("drop") : Task.CompletedTask;
            },
            delayScheduler: scheduler,
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await monitor.HandleConnectionFailedAsync(new InvalidOperationException("initial drop"));

        Assert.Equal(DevTunnelConnectionState.Connected, monitor.Status.State);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(8),
            ],
            scheduler.RecordedDelays);
    }

    [Fact]
    public async Task HandleConnectionFailedAsync_WhenMaxAttemptsExhausted_ReportsFailed()
    {
        var monitor = new DevTunnelConnectionMonitor(
            resolveEndpoint: _ => Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://t-5280.usw2.devtunnels.ms/"), null)),
            connect: (_, _) => throw new InvalidOperationException("always down"),
            delayScheduler: new RecordingDelayScheduler(),
            options: NoJitterOptions with { MaxAttempts = 3 },
            nextJitterSample: () => 0.0);

        await monitor.HandleConnectionFailedAsync(new InvalidOperationException("initial drop"));

        Assert.Equal(DevTunnelConnectionState.Failed, monitor.Status.State);
    }

    [Fact]
    public async Task HealthyConnection_PerformsNoExtraResolution()
    {
        var resolveCount = 0;
        var monitor = new DevTunnelConnectionMonitor(
            resolveEndpoint: _ => { resolveCount++; return Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://t-5280.usw2.devtunnels.ms/"), null)); },
            connect: (_, _) => Task.CompletedTask,
            delayScheduler: new RecordingDelayScheduler(),
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await monitor.StartAsync();

        // No failure reported: resolution happened exactly once (at start), none afterward.
        Assert.Equal(1, resolveCount);
    }

    private sealed class RecordingDelayScheduler : IDelayScheduler
    {
        public List<TimeSpan> RecordedDelays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            this.RecordedDelays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
