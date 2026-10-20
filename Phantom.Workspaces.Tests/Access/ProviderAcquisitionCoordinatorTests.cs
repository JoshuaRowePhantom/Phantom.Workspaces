using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class ProviderAcquisitionCoordinatorTests
{
    [Fact]
    public async Task ProviderAcquisitionCoordinator_CompatibleApprovedRows_ExecutesOnce()
    {
        await using var coordinator = new ProviderAcquisitionCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var cacheCommitted = false;
        var first = new Operation(async (context, cancellationToken) =>
        {
            Interlocked.Increment(ref executions);
            started.SetResult();
            await finish.Task.WaitAsync(cancellationToken);
            return await context.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
            {
                cacheCommitted = true;
                return ValueTask.CompletedTask;
            });
        });
        var second = new Operation((_, _) => throw new InvalidOperationException("A follower must not execute."));
        var firstTask = coordinator.ExecuteOrJoinAsync(first, Permit(first), CancellationToken.None).AsTask();
        await started.Task;
        var secondTask = coordinator.ExecuteOrJoinAsync(second, Permit(second), CancellationToken.None).AsTask();
        Assert.False(secondTask.IsCompleted);
        finish.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.True(cacheCommitted);
        Assert.Equal(1, executions);
        Assert.Equal(Assert.IsType<ProviderAcquisitionSucceeded>(results[0]).CommittedGeneration,
            Assert.IsType<ProviderAcquisitionSucceeded>(results[1]).CommittedGeneration);
    }

    [Fact]
    public async Task ProviderAcquisitionCoordinator_PendingRow_DoesNotJoinOrInheritApproval()
    {
        await using var coordinator = new ProviderAcquisitionCoordinator();
        var operation = new Operation((_, _) => throw new InvalidOperationException("Unapproved work executed."));
        var other = new Operation((_, _) => throw new InvalidOperationException());
        var permit = Permit(other);
        Assert.IsType<ProviderAcquisitionDenied>(await coordinator.ExecuteOrJoinAsync(operation, permit, CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(permit));
        Assert.False(permit.Authorizes(operation.RequestIdentifier, operation.Revision,
            operation.ExecutorKind, operation.AcquisitionGeneration, operation.CompatibilityKey));
        foreach (var invalid in new[]
        {
            new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision + 1,
                operation.ExecutorKind, operation.AcquisitionGeneration, operation.CompatibilityKey, () => true),
            new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision,
                CredentialExecutorKind.Entra, operation.AcquisitionGeneration, operation.CompatibilityKey, () => true),
            new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision,
                operation.ExecutorKind, Guid.NewGuid(), operation.CompatibilityKey, () => true),
            new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision,
                operation.ExecutorKind, operation.AcquisitionGeneration,
                operation.CompatibilityKey with { Configuration = new() { Digest = new string('e', 64) } }, () => true),
            new ProviderInteractionPermit(operation.RequestIdentifier, operation.Revision,
                operation.ExecutorKind, operation.AcquisitionGeneration, operation.CompatibilityKey, () => false),
        })
        {
            Assert.IsType<ProviderAcquisitionDenied>(
                await coordinator.ExecuteOrJoinAsync(operation, invalid, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ProviderAcquisitionCoordinator_LastParticipantCancels_TombstonesLateCompletion()
    {
        await using var coordinator = new ProviderAcquisitionCoordinator();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ProviderAcquisitionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var operation = new Operation(async (context, _) =>
        {
            started.SetResult();
            await finish.Task;
            var outcome = await context.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
            {
                commits++;
                return ValueTask.CompletedTask;
            });
            late.SetResult(outcome);
            return outcome;
        });
        var permit = Permit(operation);
        var task = coordinator.ExecuteOrJoinAsync(operation, permit, cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();
        Assert.IsType<ProviderAcquisitionCancelled>(await task);
        finish.SetResult();
        Assert.IsType<ProviderAcquisitionCancelled>(await late.Task);
        Assert.Equal(0, commits);
        Assert.IsType<ProviderAcquisitionDenied>(await coordinator.ExecuteOrJoinAsync(operation, permit, CancellationToken.None));
    }

    [Fact]
    public async Task ProviderAcquisitionCoordinator_CancelOneParticipant_DoesNotCancelApprovedFollower()
    {
        await using var coordinator = new ProviderAcquisitionCoordinator();
        using var cancellation = new CancellationTokenSource();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaderActive = true;
        ProviderInteractionPermit? executionPermit = null;
        var first = new Operation(async (context, cancellationToken) =>
        {
            executionPermit = context.InteractionPermit;
            await finish.Task.WaitAsync(cancellationToken);
            return await context.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
        });
        var second = new Operation((_, _) => throw new InvalidOperationException("Follower executed."));
        var firstTask = coordinator.ExecuteOrJoinAsync(first, Permit(first, () => leaderActive), cancellation.Token).AsTask();
        var secondTask = coordinator.ExecuteOrJoinAsync(second, Permit(second), TestContext.Current.CancellationToken).AsTask();
        leaderActive = false;
        cancellation.Cancel();
        Assert.IsType<ProviderAcquisitionCancelled>(await firstTask);
        Assert.True(executionPermit!.BelongsToGeneration(first.RequestIdentifier, first.Revision, first.AcquisitionGeneration));
        Assert.False(secondTask.IsCompleted);
        finish.SetResult();
        Assert.IsType<ProviderAcquisitionSucceeded>(await secondTask);
        Assert.False(executionPermit.BelongsToGeneration(first.RequestIdentifier, first.Revision, first.AcquisitionGeneration));
    }

    [Fact]
    public async Task ProviderAcquisitionCoordinator_CacheCommitAndCancellation_HaveAtomicPublicationBoundary()
    {
        await using var coordinator = new ProviderAcquisitionCoordinator();
        using var cancellation = new CancellationTokenSource();
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = false;
        var operation = new Operation(async (context, _) => await context.CommitAsync(new() { Value = Guid.NewGuid() }, async _ =>
        {
            committing.SetResult();
            await finishCommit.Task;
            committed = true;
        }));
        var task = coordinator.ExecuteOrJoinAsync(operation, Permit(operation), cancellation.Token).AsTask();
        await committing.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(task.IsCompleted);
        finishCommit.SetResult();
        await task;
        Assert.True(committed);
    }

    [Fact]
    public async Task ProviderAcquisitionCoordinator_UncommittedSuccess_IsRejectedAsProgrammingFailure()
    {
        await using var coordinator = new ProviderAcquisitionCoordinator();
        var operation = new Operation((_, _) => ValueTask.FromResult<ProviderAcquisitionOutcome>(
            new ProviderAcquisitionSucceeded { CommittedGeneration = new() { Value = Guid.NewGuid() } }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.ExecuteOrJoinAsync(operation, Permit(operation), TestContext.Current.CancellationToken));
    }

    internal static ProviderInteractionPermit Permit(
        IProviderAcquisitionOperation operation,
        Func<bool>? isCurrent = null) => new(
        operation.RequestIdentifier,
        operation.Revision,
        operation.ExecutorKind,
        operation.AcquisitionGeneration,
        operation.CompatibilityKey,
        isCurrent ?? (() => true));

    private sealed class Operation(Func<ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>> execute)
        : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey { get; } = new()
        {
            Configuration = new() { Digest = new string('a', 64) },
            Source = new() { Digest = new string('b', 64) },
            Protocol = ProviderProtocolKind.OAuth,
        };
        public UserAuthorizationRequestIdentifier RequestIdentifier { get; } = new() { Value = Guid.NewGuid() };
        public long Revision => 1;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.OAuth;
        public Guid AcquisitionGeneration { get; } = Guid.NewGuid();
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => execute(commitContext, cancellationToken);
    }
}
