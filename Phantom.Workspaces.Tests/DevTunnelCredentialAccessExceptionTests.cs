using System.Net.Http;
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
    public async Task RequireAsync_CancelledLateSuccessPreservesCleanupFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cleanupFailure = new InvalidOperationException("Cleanup failed.");
        var result = new Result { Cleanup = () => ValueTask.FromException(cleanupFailure) };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevTunnelCredentialAccessException.RequireAsync(
                new AccessSucceeded<Result> { Result = result }, cancellation.Token).AsTask());
        Assert.Same(cleanupFailure, exception);
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

    [Fact]
    public async Task RequireUseAsync_CallerCancellationPrecedesGuardFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelCredentialAccessException.RequireUseAsync(new SensitiveValueUseFailed<int>
            {
                ErrorCode = AccessErrorCode.InvalidGeneration,
                SafeMessage = "Credential generation is unavailable.",
            }, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RequireUseAsync_CancelledLateSuccessAwaitsAsyncDisposalInsteadOfSynchronousDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new Result { Cleanup = () => new ValueTask(cleanup.Task) };
        var pending = DevTunnelCredentialAccessException.RequireUseAsync(
            new SensitiveValueUseSucceeded<Result> { Result = result }, cancellation.Token).AsTask();
        Assert.Equal(1, result.Disposals);
        Assert.Equal(0, result.SynchronousDisposals);
        Assert.False(pending.IsCompleted);
        cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, result.Disposals);
        Assert.Equal(0, result.SynchronousDisposals);
    }

    [Fact]
    public async Task RequireUseAsync_CancelledLateHttpResponseIsDisposedExactlyOnce()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var content = new TrackedContent();
        var response = new HttpResponseMessage { Content = content };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelCredentialAccessException.RequireUseAsync(
                new SensitiveValueUseSucceeded<HttpResponseMessage> { Result = response }, cancellation.Token).AsTask());
        Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task RequireUseAsync_CancelledLateSuccessPreservesCleanupFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cleanupFailure = new InvalidOperationException("Cleanup failed.");
        var result = new Result { Cleanup = () => ValueTask.FromException(cleanupFailure) };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevTunnelCredentialAccessException.RequireUseAsync(
                new SensitiveValueUseSucceeded<Result> { Result = result }, cancellation.Token).AsTask());
        Assert.Same(cleanupFailure, exception);
        Assert.Equal(1, result.Disposals);
        Assert.Equal(0, result.SynchronousDisposals);
    }

    [Fact]
    public async Task RequireUseAsync_UncancelledResponseRemainsCallerOwned()
    {
        var content = new TrackedContent();
        using var response = new HttpResponseMessage { Content = content };
        var published = await DevTunnelCredentialAccessException.RequireUseAsync(
            new SensitiveValueUseSucceeded<HttpResponseMessage> { Result = response },
            TestContext.Current.CancellationToken);
        Assert.Same(response, published);
        Assert.Equal(0, content.Disposals);
    }

    [Fact]
    public async Task RequireUseAsync_UncancelledAuthorizationFailureRetainsRequiredAction()
    {
        var identifier = new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() };
        var exception = await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() =>
            DevTunnelCredentialAccessException.RequireUseAsync(new SensitiveValueUseAuthorizationRequired<int>
            {
                RequestIdentifier = identifier,
            }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(identifier, exception.RequiredAction);
    }

    private sealed class Result : IAccessResult, IDisposable
    {
        public Func<ValueTask>? Cleanup { get; init; }
        public int Disposals { get; private set; }
        public int SynchronousDisposals { get; private set; }

        public void Dispose() => this.SynchronousDisposals++;

        public ValueTask DisposeAsync()
        {
            this.Disposals++;
            return this.Cleanup?.Invoke() ?? ValueTask.CompletedTask;
        }
    }

    private sealed class TrackedContent() : ByteArrayContent([])
    {
        public int Disposals { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { this.Disposals++; }
            base.Dispose(disposing);
        }
    }
}
