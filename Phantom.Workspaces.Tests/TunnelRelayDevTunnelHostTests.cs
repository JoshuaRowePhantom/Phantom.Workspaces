using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Ssh;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for <see cref="TunnelRelayDevTunnelHost"/>'s source-site handling of the terminal
/// shutdown exceptions produced by the Dev Tunnels SDK's fire-and-forget background work when
/// the relay host is disposed mid-flight (issue #1301). The wrapper's <c>StopAsync</c> routes
/// through the internal <c>DisposeRelayHostSafelyAsync</c> helper; we exercise the helper and
/// the classification predicate directly through the <c>InternalsVisibleTo</c>-exposed surface
/// so we do not need to spin up a real SDK relay host to reproduce the fault modes.
/// </summary>
public sealed class TunnelRelayDevTunnelHostTests
{
    [Fact]
    public async Task DisposeRelayHostSafelyAsync_ConsumesSocketException_OperationAborted()
    {
        var host = new FaultingDisposable(new SocketException((int)SocketError.OperationAborted));

        await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_ConsumesSshConnectionException()
    {
        var host = new FaultingDisposable(new SshConnectionException("SshServerSession disposed."));

        await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_ConsumesObjectDisposedException()
    {
        var host = new FaultingDisposable(new ObjectDisposedException("TunnelRelayTunnelHost"));

        await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_ConsumesOperationCanceledException()
    {
        var host = new FaultingDisposable(new OperationCanceledException());

        await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_ConsumesAggregateOfExpectedShutdownExceptions()
    {
        var host = new FaultingDisposable(new AggregateException(
            new SocketException((int)SocketError.OperationAborted),
            new ObjectDisposedException("TunnelRelayTunnelHost"),
            new SshConnectionException("SshServerSession disposed.")));

        await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_RethrowsUnexpectedException()
    {
        var host = new FaultingDisposable(new InvalidOperationException("real defect"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host));

        Assert.Equal("real defect", exception.Message);
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_RethrowsAggregateWithUnexpectedInner()
    {
        // A mixed AggregateException — one expected shutdown exception plus one genuine defect —
        // must surface, not be silently consumed.
        var host = new FaultingDisposable(new AggregateException(
            new SocketException((int)SocketError.OperationAborted),
            new InvalidOperationException("real defect")));

        await Assert.ThrowsAsync<AggregateException>(
            () => TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host));
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_ForwardsDisposeToUnderlyingObject()
    {
        var host = new FaultingDisposable(exception: null);

        await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void IsExpectedShutdownException_RejectsUnrelatedException()
    {
        Assert.False(TunnelRelayDevTunnelHost.IsExpectedShutdownException(new InvalidOperationException()));
        Assert.False(TunnelRelayDevTunnelHost.IsExpectedShutdownException(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.False(TunnelRelayDevTunnelHost.IsExpectedShutdownException(new AggregateException()));
    }

    [Fact]
    public void IsExpectedShutdownException_SocketExceptionOperationAborted_ReturnsTrue()
    {
        // Issue #1350: the SDK's aborted in-flight TcpClient connect faults with SocketError
        // .OperationAborted (995); the classifier must treat it as an expected shutdown outcome.
        Assert.True(TunnelRelayDevTunnelHost.IsExpectedShutdownException(
            new SocketException((int)SocketError.OperationAborted)));
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_PendingTcpClientConnectAbortedDuringShutdown_DoesNotLeakUnobserved()
    {
        // Issue #1350: the SDK bridges each incoming relay channel to the local port with a
        // fire-and-forget TcpClient connect that is NOT returned by DisposeAsync. Cancelling the
        // shutdown token BEFORE disposal (as the wrapper does) makes that connect unwind as *Canceled*
        // instead of *Faulted* with SocketError.OperationAborted, so nothing reaches the finalizer.
        Exception? unobserved = null;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobserved = args.Exception;
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var cts = new CancellationTokenSource();
            var host = new TcpConnectSpawningDisposable(cts.Token);

            await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts);

            await host.ConnectCompleted;
            host = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public void ObserveForwardedConnectTransform_ForwarderConnectFaultsWithOperationAborted_IsConsumedAtSource()
    {
        // Issue #1350: a forwarded-port transform/connect that faults with an expected shutdown outcome
        // must be observed at the SDK-owning site (via ForwardedPortConnecting), never on the finalizer.
        Exception? unobserved = null;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobserved = args.Exception;
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var faulted = Task.FromException<System.IO.Stream?>(
                new SocketException((int)SocketError.OperationAborted));

            TunnelRelayDevTunnelHost.ObserveForwardedConnectTransform(faulted);
            faulted = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public void ObserveForwardedConnectTransform_GenuineTransformFault_IsReSurfaced()
    {
        // A non-shutdown fault surfacing through the transform pipeline must NOT be silently swallowed;
        // ObserveForwardedConnectTransform re-raises it so real defects still reach the crash handlers.
        Exception? unobserved = null;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobserved = args.Exception;
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var faulted = Task.FromException<System.IO.Stream?>(new InvalidOperationException("real defect"));

            TunnelRelayDevTunnelHost.ObserveForwardedConnectTransform(faulted);
            faulted = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.NotNull(unobserved);
            var inners = (unobserved as AggregateException)?.Flatten().InnerExceptions
                ?? (System.Collections.Generic.IReadOnlyList<Exception>)new[] { unobserved! };
            Assert.Contains(inners, inner => inner is InvalidOperationException { Message: "real defect" });
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task DisposeRelayHostSafelyAsync_UnobservedBackgroundFault_DoesNotSurfaceOnFinalizer()
    {
        // Regression: prior to the fix, the SDK's DisposeAsync would surface a terminal exception
        // synchronously that the caller re-threw, and the process's unhandled-exception path would
        // trip. Here we verify that the safe-dispose helper consumes it so no exception is observed
        // on the TaskScheduler's unobserved-task-exception path either.
        Exception? unobserved = null;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobserved = args.Exception;
            args.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var host = new FaultingDisposable(new SocketException((int)SocketError.OperationAborted));
            await TunnelRelayDevTunnelHost.DisposeRelayHostSafelyAsync(host);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_CancelsShutdownTokenBeforeDisposingRelayHost()
    {
        // Issue #1322: the shutdown token must be cancelled strictly BEFORE the relay host's
        // DisposeAsync is entered, so any in-flight SshSession.RequestAsync observes cancellation
        // rather than racing disposal into ObjectDisposedException("SshSession").
        var cts = new CancellationTokenSource();
        var host = new TokenObservingDisposable(cts.Token);

        await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts);

        Assert.Equal(1, host.DisposeCount);
        Assert.True(host.TokenWasCancelledAtDispose);
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_DisposesShutdownCtsAfterDispose()
    {
        var cts = new CancellationTokenSource();
        var host = new TokenObservingDisposable(cts.Token);

        await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts);

        // The token source is disposed by the helper — Cancel() now throws ObjectDisposedException.
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_NullShutdownCts_DisposesHostWithoutThrowing()
    {
        // StartAsync may not have created a token source (e.g. StopAsync before a successful Start).
        var host = new FaultingDisposable(exception: null);

        await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, shutdownCts: null);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_AlreadyDisposedShutdownCts_DoesNotThrow()
    {
        // A prior/concurrent teardown may already have disposed the token source; cancelling it again
        // must be swallowed, and the host must still be disposed.
        var cts = new CancellationTokenSource();
        cts.Dispose();
        var host = new FaultingDisposable(exception: null);

        await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_ConsumesExpectedShutdownException()
    {
        var cts = new CancellationTokenSource();
        var host = new FaultingDisposable(new ObjectDisposedException("SshSession"));

        await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts);

        Assert.Equal(1, host.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_RethrowsUnexpectedInner_AndStillDisposesCts()
    {
        // A genuine defect surfacing through DisposeAsync must still propagate, but the token source
        // must be disposed regardless (finally).
        var cts = new CancellationTokenSource();
        var host = new FaultingDisposable(new InvalidOperationException("real defect"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts));

        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Fact]
    public async Task CancelAndDisposeRelayHostSafelyAsync_PendingSshRequestCancelledBeforeDispose_DoesNotLeakUnobserved()
    {
        // Issue #1322 core regression: the SDK's in-flight SshSession.RequestAsync is a fire-and-forget
        // Task NOT returned by DisposeAsync (so #1301's guard cannot observe it). Cancelling the shutdown
        // token BEFORE disposal makes that task complete as *Canceled* (which is never surfaced as an
        // unobserved *faulted* task) instead of *Faulted* with ObjectDisposedException("SshSession").
        Exception? unobserved = null;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobserved = args.Exception;
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var cts = new CancellationTokenSource();
            var host = new RequestSpawningDisposable(cts.Token);

            await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(host, cts);

            // Deterministically wait for the fire-and-forget "request" task to finish (without
            // observing its own completion Task's exception), then drop it and force finalization.
            await host.RequestCompleted;
            host = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task TunnelRelayDevTunnelHost_WhenSdkStatusBecomesDisconnected_IsRunningBecomesFalse()
    {
        // Issue #1375: a simulated SDK ConnectionStatusChanged -> Disconnected (not during our shutdown)
        // must flip IsRunning to false so the stale-true signal is fixed. MaxAttempts:0 keeps the
        // reconnect from immediately re-establishing, so we can observe the dead state deterministically.
        var sessions = new List<FakeRelayHostSession>();
        var host = new TunnelRelayDevTunnelHost(
            connectSessionAsync: (_, _, _) =>
            {
                var session = new FakeRelayHostSession();
                sessions.Add(session);
                return Task.FromResult<IRelayHostSession>(session);
            },
            delayScheduler: new ImmediateDelayScheduler(),
            reconnectOptions: new DevTunnelReconnectOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), MaxAttempts: 0, JitterFraction: 0.0),
            nextJitterSample: () => 0.0);

        await host.StartAsync("tunnel-123", localPort: 5280, TestContext.Current.CancellationToken);
        Assert.True(host.IsRunning);

        sessions[0].RaiseDisconnected(new RelayHostDisconnectInfo(TooManyConnections: false, ErrorMessage: "relay closed"));
        if (host.ReconnectTask is not null)
        {
            await host.ReconnectTask;
        }

        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task TunnelRelayDevTunnelHost_WhenDisconnectedUnexpectedly_TriggersReconnect()
    {
        // Issue #1375: an unexpected Disconnected (reason not our teardown) must drive a full reconnect
        // — a fresh connect-ready tunnel fetch + new SDK host + ConnectAsync — re-establishing hosting
        // without an application restart.
        var connectCalls = new List<(string TunnelId, int LocalPort)>();
        var sessions = new List<FakeRelayHostSession>();
        var host = new TunnelRelayDevTunnelHost(
            connectSessionAsync: (tunnelId, localPort, _) =>
            {
                connectCalls.Add((tunnelId, localPort));
                var session = new FakeRelayHostSession();
                sessions.Add(session);
                return Task.FromResult<IRelayHostSession>(session);
            },
            delayScheduler: new ImmediateDelayScheduler(),
            reconnectOptions: new DevTunnelReconnectOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), MaxAttempts: null, JitterFraction: 0.0),
            nextJitterSample: () => 0.0);

        await host.StartAsync("tunnel-123", localPort: 5280, TestContext.Current.CancellationToken);
        Assert.Single(connectCalls);

        sessions[0].RaiseDisconnected(new RelayHostDisconnectInfo(TooManyConnections: false, ErrorMessage: "relay closed"));
        Assert.NotNull(host.ReconnectTask);
        await host.ReconnectTask!;

        // The full connect sequence ran again with the same tunnel identity, producing a fresh session.
        Assert.Equal(2, connectCalls.Count);
        Assert.Equal(("tunnel-123", 5280), connectCalls[1]);
        Assert.Equal(1, sessions[0].DisposeCount);
        Assert.True(host.IsRunning);
    }

    private sealed class FakeRelayHostSession : IRelayHostSession
    {
        public event EventHandler<RelayHostDisconnectInfo>? Disconnected;

        public int DisposeCount { get; private set; }

        public void RaiseDisconnected(RelayHostDisconnectInfo info) => this.Disconnected?.Invoke(this, info);

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateDelayScheduler : IDelayScheduler
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TokenObservingDisposable : IAsyncDisposable
    {
        private readonly CancellationToken shutdownToken;

        public TokenObservingDisposable(CancellationToken shutdownToken)
        {
            this.shutdownToken = shutdownToken;
        }

        public int DisposeCount { get; private set; }

        public bool TokenWasCancelledAtDispose { get; private set; }

        public async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            this.TokenWasCancelledAtDispose = this.shutdownToken.IsCancellationRequested;
            await Task.Yield();
        }
    }

    private sealed class RequestSpawningDisposable : IAsyncDisposable
    {
        private readonly CancellationToken shutdownToken;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RequestSpawningDisposable(CancellationToken shutdownToken)
        {
            this.shutdownToken = shutdownToken;
        }

        /// <summary>Completes when the fire-and-forget "in-flight request" task has finished.</summary>
        public Task RequestCompleted => this.completed.Task;

        public ValueTask DisposeAsync()
        {
            // Model the SDK's in-flight SshSession.RequestAsync: a fire-and-forget Task that is NOT
            // returned to / awaited by us. It observes the shutdown token — cancelled => the Task
            // becomes Canceled (benign); otherwise it faults with ObjectDisposedException("SshSession").
            _ = Task.Run(() =>
            {
                try
                {
                    this.shutdownToken.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException("SshSession");
                }
                finally
                {
                    this.completed.SetResult();
                }
            });

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TcpConnectSpawningDisposable : IAsyncDisposable
    {
        private readonly CancellationToken shutdownToken;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TcpConnectSpawningDisposable(CancellationToken shutdownToken)
        {
            this.shutdownToken = shutdownToken;
        }

        /// <summary>Completes when the fire-and-forget "connect" task has finished.</summary>
        public Task ConnectCompleted => this.completed.Task;

        public ValueTask DisposeAsync()
        {
            // Model the SDK's fire-and-forget local TcpClient connect (issue #1350): a Task NOT
            // returned to / awaited by us. It observes the shutdown token — cancelled => the Task
            // becomes Canceled (benign); otherwise it faults with SocketError.OperationAborted.
            _ = Task.Run(() =>
            {
                try
                {
                    this.shutdownToken.ThrowIfCancellationRequested();
                    throw new SocketException((int)SocketError.OperationAborted);
                }
                finally
                {
                    this.completed.SetResult();
                }
            });

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingDisposable : IAsyncDisposable
    {
        private readonly Exception? exception;

        public FaultingDisposable(Exception? exception)
        {
            this.exception = exception;
        }

        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            await Task.Yield();
            if (this.exception is not null)
            {
                throw this.exception;
            }
        }
    }
}
