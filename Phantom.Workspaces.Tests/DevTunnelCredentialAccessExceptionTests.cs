using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelCredentialAccessExceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequireAsync_CancellationAndShutdownRemainCancellation(bool shutdown)
    {
        AccessOutcome<Result> outcome = shutdown ? new AccessShuttingDown<Result>() : new AccessCancelled<Result>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelCredentialAccessException.RequireAsync(outcome, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RequireAsync_CallerCancellationPrecedesProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelCredentialAccessException.RequireAsync(new AccessFailed<Result>
            {
                ErrorCode = AccessErrorCode.ProviderFailure,
            }, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RequireAsync_CancelledLateSuccessAwaitsExactlyOneResultDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new Result { Cleanup = () => new ValueTask(cleanup.Task) };
        var pending = DevTunnelCredentialAccessException.RequireAsync(
            new AccessSucceeded<Result> { Result = result }, cancellation.Token).AsTask();
        Assert.Equal(1, result.Disposals);
        Assert.False(pending.IsCompleted);
        cleanup.SetResult();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, result.Disposals);
    }

    [Fact]
    public async Task RequireAsync_CancelledLateSuccessCleanupFailureDoesNotBecomeAuthorizationFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var result = new Result { Cleanup = () => ValueTask.FromException(new InvalidOperationException()) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelCredentialAccessException.RequireAsync(
                new AccessSucceeded<Result> { Result = result }, cancellation.Token).AsTask());
        Assert.Equal(1, result.Disposals);
    }

    [Fact]
    public async Task RequireAsync_UncancelledSuccessTransfersResultOwnership()
    {
        var result = new Result();
        var published = await DevTunnelCredentialAccessException.RequireAsync(
            new AccessSucceeded<Result> { Result = result }, TestContext.Current.CancellationToken);
        Assert.Same(result, published);
        Assert.Equal(0, result.Disposals);
        await published.DisposeAsync();
        Assert.Equal(1, result.Disposals);
    }

    [Fact]
    public async Task RequireAsync_UncancelledFailureRetainsTypedError()
    {
        var exception = await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() =>
            DevTunnelCredentialAccessException.RequireAsync(new AccessFailed<Result>
            {
                ErrorCode = AccessErrorCode.IdentityMismatch,
            }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(AccessErrorCode.IdentityMismatch, exception.ErrorCode);
    }

    private sealed class Result : IAccessResult
    {
        public Func<ValueTask>? Cleanup { get; init; }
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.Disposals++;
            return this.Cleanup?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
