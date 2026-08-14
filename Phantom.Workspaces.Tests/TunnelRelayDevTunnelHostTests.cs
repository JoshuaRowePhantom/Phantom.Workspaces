using System;
using System.Net.Sockets;
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
