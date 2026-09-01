using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for <see cref="DevTunnelHostConnectionMonitor"/> — the relay-HOST reconnect monitor added for
/// issue #1375. Mirrors <see cref="DevTunnelConnectionMonitorTests"/>: deterministic via an injected
/// <see cref="IDelayScheduler"/> and fixed jitter (no real timers), and additionally verifies the
/// TooManyConnections terminal-failure path (no reconnect-war).
/// </summary>
public sealed class DevTunnelHostConnectionMonitorTests
{
    private static readonly DevTunnelReconnectOptions NoJitterOptions =
        new(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(8), MaxAttempts: null, JitterFraction: 0.0);

    [Fact]
    public async Task RelayHostMonitor_OnDisconnect_ReconnectsWithBackoff()
    {
        var scheduler = new RecordingDelayScheduler();
        var connectAttempts = 0;
        var monitor = new DevTunnelHostConnectionMonitor(
            connect: _ =>
            {
                connectAttempts++;
                // Fail the first 4 reconnect attempts, succeed on the 5th — re-establishing the relay
                // without an application restart.
                return connectAttempts < 5 ? throw new InvalidOperationException("relay drop") : Task.CompletedTask;
            },
            delayScheduler: scheduler,
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await monitor.HandleDisconnectAsync(tooManyConnections: false, TestContext.Current.CancellationToken);

        Assert.Equal(DevTunnelConnectionState.Connected, monitor.State);
        Assert.Equal(5, connectAttempts);
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
    public async Task RelayHostMonitor_WhenTooManyConnections_DoesNotReconnect_SurfacesFailed()
    {
        var scheduler = new RecordingDelayScheduler();
        var connectAttempts = 0;
        var monitor = new DevTunnelHostConnectionMonitor(
            connect: _ => { connectAttempts++; return Task.CompletedTask; },
            delayScheduler: scheduler,
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);

        await monitor.HandleDisconnectAsync(tooManyConnections: true, TestContext.Current.CancellationToken);

        // Another host claimed the tunnel: surface Failed and do NOT reconnect-war.
        Assert.Equal(DevTunnelConnectionState.Failed, monitor.State);
        Assert.Equal(0, connectAttempts);
        Assert.Empty(scheduler.RecordedDelays);
    }

    [Fact]
    public async Task RelayHostMonitor_WhenMaxAttemptsExhausted_ReportsFailed()
    {
        var monitor = new DevTunnelHostConnectionMonitor(
            connect: _ => throw new InvalidOperationException("always down"),
            delayScheduler: new RecordingDelayScheduler(),
            options: NoJitterOptions with { MaxAttempts = 3 },
            nextJitterSample: () => 0.0);

        await monitor.HandleDisconnectAsync(tooManyConnections: false, TestContext.Current.CancellationToken);

        Assert.Equal(DevTunnelConnectionState.Failed, monitor.State);
    }

    [Fact]
    public async Task RelayHostMonitor_RaisesStateChangedTransitions()
    {
        var states = new List<DevTunnelConnectionState>();
        var connectAttempts = 0;
        var monitor = new DevTunnelHostConnectionMonitor(
            connect: _ =>
            {
                connectAttempts++;
                return connectAttempts < 2 ? throw new InvalidOperationException("drop") : Task.CompletedTask;
            },
            delayScheduler: new RecordingDelayScheduler(),
            options: NoJitterOptions,
            nextJitterSample: () => 0.0);
        monitor.StateChanged += (_, state) => states.Add(state);

        await monitor.HandleDisconnectAsync(tooManyConnections: false, TestContext.Current.CancellationToken);

        Assert.Contains(DevTunnelConnectionState.Reconnecting, states);
        Assert.Equal(DevTunnelConnectionState.Connected, states[^1]);
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
