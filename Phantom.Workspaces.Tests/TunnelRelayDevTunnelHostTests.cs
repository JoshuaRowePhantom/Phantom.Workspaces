using System;
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
