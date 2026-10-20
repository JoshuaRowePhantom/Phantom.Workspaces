using System.IO;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class CredentialAccessExecutorRegistryTests
{
    [Fact]
    public async Task CredentialAccessExecutorRegistry_DisposeAsync_AttemptsEveryRegistrationAndPreservesFailures()
    {
        var firstFailure = new InvalidOperationException("First executor cleanup failed.");
        var thirdFailure = new InvalidOperationException("Third executor cleanup failed.");
        var first = new CleanupExecutor<FirstRequest>(() => ValueTask.FromException(firstFailure));
        var second = new CleanupExecutor<SecondRequest>(() => ValueTask.CompletedTask);
        var third = new CleanupExecutor<ThirdRequest>(() => ValueTask.FromException(thirdFailure));
        var registry = new CredentialAccessExecutorRegistry();
        registry.Add<FirstRequest, Result>(first);
        registry.Add<SecondRequest, Result>(second);
        registry.Add<ThirdRequest, Result>(third);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => registry.DisposeAsync().AsTask());

        Assert.Equal(new Exception[] { firstFailure, thirdFailure }, failure.InnerExceptions);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, third.Disposals);
        var repeated = await Assert.ThrowsAsync<AggregateException>(() => registry.DisposeAsync().AsTask());
        Assert.Same(failure, repeated);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, third.Disposals);
    }

    [Fact]
    public async Task AccessBroker_DisposeAsync_ConcurrentAndRepeatedFailureStillCleansLaterExecutorsAndPolicy()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executorFailure = new InvalidOperationException("Executor cleanup failed.");
        var first = new CleanupExecutor<FirstRequest>(async () =>
        {
            started.SetResult();
            await finish.Task;
            throw executorFailure;
        });
        var second = new CleanupExecutor<SecondRequest>(() => ValueTask.CompletedTask);
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<FirstRequest, Result>(first);
        executors.Add<SecondRequest, Result>(second);
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(
            Path.Combine(Environment.CurrentDirectory, ".access-cleanup-tests", $"{Guid.NewGuid():N}.json")));
        var request = ReleaseAuthorizationContractTests.Request();
        request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
        var authorization = await policy.CommitAsync(new()
        {
            Request = request,
            Scope = ReleaseMemoryScopeKind.AllowOnce,
        }, TestContext.Current.CancellationToken);
        var registry = new UserAuthorizationRegistry();
        var broker = new AccessBroker(policy, executors, new ProviderAcquisitionCoordinator(), registry);

        var firstDisposal = broker.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var concurrentDisposal = broker.DisposeAsync().AsTask();
        var concurrentWaited = !concurrentDisposal.IsCompleted;
        finish.SetResult();
        var failure = await Assert.ThrowsAsync<AggregateException>(() => firstDisposal);
        var concurrentFailure = await Assert.ThrowsAsync<AggregateException>(() => concurrentDisposal);
        var repeatedFailure = await Assert.ThrowsAsync<AggregateException>(() => broker.DisposeAsync().AsTask());

        Assert.True(concurrentWaited);
        Assert.Same(executorFailure, Assert.Single(failure.InnerExceptions));
        Assert.Same(failure, concurrentFailure);
        Assert.Same(failure, repeatedFailure);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch,
            await policy.ValidateAsync(authorization.Identifier, request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(registry.GetSnapshot());
    }

    private sealed record FirstRequest : CredentialAccessRequest<Result>;
    private sealed record SecondRequest : CredentialAccessRequest<Result>;
    private sealed record ThirdRequest : CredentialAccessRequest<Result>;

    private sealed class Result : ICredentialAccessResult
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleanupExecutor<TRequest>(Func<ValueTask> cleanup)
        : ICredentialAccessExecutor<TRequest, Result>, IAsyncDisposable
        where TRequest : CredentialAccessRequest<Result>
    {
        public int Disposals { get; private set; }
        public CredentialExecutorKind Kind => CredentialExecutorKind.StoredSecret;

        public ValueTask<CredentialInspection> InspectAsync(
            TRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException("Cleanup test does not inspect credentials.");

        public ValueTask<CredentialExecutionOutcome<Result>> ExecuteAsync(
            TRequest request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException("Cleanup test does not execute credentials.");

        public ValueTask DisposeAsync()
        {
            this.Disposals++;
            return cleanup();
        }
    }
}
