using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class EntraCredentialSessionTests
{
    [Fact]
    public async Task EntraCredentialSession_InspectCacheAsync_DoesNotCallAzureIdentity()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var inspection = await session.InspectCacheAsync(request, CancellationToken.None);
        Assert.Null(inspection.Generation);
        Assert.Equal(0, acquirer.Calls);
        Assert.Equal(request.CreateLogicalIdentifier(), inspection.LogicalIdentifier);
        Assert.Equal(request.CreateSourceIdentifier(), inspection.SourceIdentifier);
    }

    [Fact]
    public async Task EntraCredentialSession_ExecuteAsync_RequiresMatchingPermit()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var key = (await session.InspectCacheAsync(request, CancellationToken.None)).AcquisitionKey;
        var rawPermit = Permit(key);
        var wrongKey = key with { Protocol = ProviderProtocolKind.OAuth };
        foreach (var permit in new[] { rawPermit, Permit(wrongKey).BindExecution(() => true), Permit(key).BindExecution(() => false) })
        {
            var commit = new ProviderAcquisitionCommitContext(() => true, default, new SemaphoreSlim(1, 1), permit);
            Assert.IsType<ProviderAcquisitionDenied>(await session.ExecuteAsync(request, commit, CancellationToken.None));
        }

        Assert.Equal(0, acquirer.Calls);
        Assert.Null(await session.InspectGenerationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EntraPinnedTokenProvider_Commit_PublishesGenerationAfterCacheWrite()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var key = (await session.InspectCacheAsync(request, CancellationToken.None)).AcquisitionKey;
        using var gate = new SemaphoreSlim(0, 1);
        var commit = new ProviderAcquisitionCommitContext(() => true, default, gate, Permit(key).BindExecution(() => true));
        var acquisition = session.ExecuteAsync(request, commit, CancellationToken.None).AsTask();
        Assert.False(acquisition.IsCompleted);
        Assert.Null(await session.InspectGenerationAsync(CancellationToken.None));
        Assert.Null(commit.CommittedGeneration);
        gate.Release();
        var receipt = Assert.IsType<ProviderAcquisitionSucceeded>(await acquisition);
        var generation = await session.InspectGenerationAsync(CancellationToken.None);
        Assert.Equal(receipt.CommittedGeneration, generation!.Generation);
        Assert.Equal(acquirer.Account, generation.AccountIdentifier);
        var export = EntraSessionTestAuthorization.Export(request, acquirer.Account);
        using var material = await session.CopyGenerationAsync(
            receipt.CommittedGeneration, export.Authorization, export.Context, CancellationToken.None);
        Assert.Equal("private-token", new string(material!.Token));
    }

    [Fact]
    public async Task EntraCredentialSession_InvalidationNeverDeletesNewerGeneration()
    {
        var request = Request();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), new Acquirer());
        async Task<CredentialMaterialGenerationIdentifier> AcquireAsync()
        {
            var key = (await session.InspectCacheAsync(request, CancellationToken.None)).AcquisitionKey;
            using var gate = new SemaphoreSlim(1, 1);
            var context = new ProviderAcquisitionCommitContext(() => true, default, gate, Permit(key).BindExecution(() => true));
            return Assert.IsType<ProviderAcquisitionSucceeded>(
                await session.ExecuteAsync(request, context, CancellationToken.None)).CommittedGeneration;
        }

        var first = await AcquireAsync();
        var second = await AcquireAsync();
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await session.InvalidateGenerationAsync(first, CancellationToken.None));
        Assert.True(await session.IsCurrentGenerationAsync(second, CancellationToken.None));
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await session.InvalidateGenerationAsync(second, CancellationToken.None));
        Assert.False(await session.IsCurrentGenerationAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task EntraCredentialSession_TombstonedCommitCannotPublishAndProviderErrorsAreSafe()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var key = (await session.InspectCacheAsync(request, CancellationToken.None)).AcquisitionKey;
        using var gate = new SemaphoreSlim(1, 1);
        var commit = new ProviderAcquisitionCommitContext(() => false, default, gate, Permit(key).BindExecution(() => true));
        Assert.IsType<ProviderAcquisitionCancelled>(await session.ExecuteAsync(request, commit, CancellationToken.None));
        Assert.Null(await session.InspectGenerationAsync(CancellationToken.None));
        acquirer.Failure = new AuthenticationFailedException("private-account private-token");
        commit = new ProviderAcquisitionCommitContext(() => true, default, gate, Permit(key).BindExecution(() => true));
        var failure = Assert.IsType<ProviderAcquisitionFailed>(await session.ExecuteAsync(request, commit, CancellationToken.None));
        Assert.Equal(AccessErrorCode.ProviderFailure, failure.ErrorCode);
        Assert.DoesNotContain("private", failure.ToString());
    }

    internal static ProviderInteractionPermit Permit(ProviderAcquisitionKey key) =>
        new(new() { Value = Guid.NewGuid() }, 1, CredentialExecutorKind.Entra, Guid.NewGuid(), key, () => true);

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_ZeroesBorrowedMemoryOnReturnAndException()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        ReadOnlyMemory<char> observed = default;
        var outcome = await session.UseGenerationAsync(generation, capability.Context, capability.Permit, (material, _) =>
        {
            observed = material;
            Assert.Equal("private-token", new string(material.Span));
            return ValueTask.FromResult(material.Length);
        }, CancellationToken.None);
        Assert.Equal("private-token".Length, Assert.IsType<EntraGenerationUseSucceeded<int>>(outcome).Result);
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.UseGenerationAsync<int>(generation, capability.Context, capability.Permit, (material, _) =>
            {
                observed = material;
                throw new InvalidOperationException("callback failed");
            }, CancellationToken.None));
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        Assert.Equal(1, acquirer.Calls);
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_CancellationZeroesMemoryWithoutDeletingCache()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<char> observed = default;
        var pending = session.UseGenerationAsync<int>(generation, capability.Context, capability.Permit, async (material, cancellationToken) =>
        {
            observed = material;
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }, cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        Assert.True(await session.IsCurrentGenerationAsync(generation, CancellationToken.None));
        Assert.Equal(1, acquirer.Calls);
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_DisposalCancelsAndWaitsForCallbackCleanup()
    {
        var request = Request();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), new Acquirer());
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<char> observed = default;
        var pending = session.UseGenerationAsync<int>(generation, capability.Context, capability.Permit, async (material, cancellationToken) =>
        {
            observed = material;
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await finish.Task;
            return 1;
        }, CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = session.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(disposal.IsCompleted);
        finish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        Assert.IsType<EntraGenerationUseUnavailable<int>>(await session.UseGenerationAsync<int>(
            generation, capability.Context, capability.Permit,
            (_, _) => throw new InvalidOperationException("Disposed session cannot run."), CancellationToken.None));
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_RotationNeverSubstitutesCurrentGeneration()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<char> observed = default;
        var pending = session.UseGenerationAsync<int>(generation, capability.Context, capability.Permit, async (material, _) =>
        {
            observed = material;
            started.TrySetResult();
            await finish.Task;
            Assert.Equal("private-token", new string(material.Span));
            return material.Length;
        }, CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = await AcquireAsync(session, request);
        Assert.NotEqual(generation, replacement);
        finish.TrySetResult();
        Assert.IsType<EntraGenerationUseSucceeded<int>>(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        Assert.IsType<EntraGenerationUseUnavailable<int>>(await session.UseGenerationAsync<int>(
            generation, capability.Context, capability.Permit,
            (_, _) => throw new InvalidOperationException("A stale receipt cannot read a replacement."), CancellationToken.None));
        Assert.IsType<EntraGenerationUseSucceeded<int>>(await session.UseGenerationAsync(
            replacement, capability.Context, capability.Permit,
            (material, _) => ValueTask.FromResult(material.Length), CancellationToken.None));
        Assert.Equal(2, acquirer.Calls);
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_ExactExpiryAndPreCancellationCannotRunCallback()
    {
        var request = Request();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var acquirer = new Acquirer { ExpiresOn = clock.GetUtcNow().AddHours(1) };
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer, clock);
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.UseGenerationAsync<int>(
                generation, capability.Context, capability.Permit,
                (_, _) => throw new InvalidOperationException("Cancelled use cannot run."), cancellation.Token));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsType<EntraGenerationUseUnavailable<int>>(await session.UseGenerationAsync<int>(
            generation, capability.Context, capability.Permit,
            (_, _) => throw new InvalidOperationException("Expired use cannot run."), CancellationToken.None));
        Assert.Equal(1, acquirer.Calls);
    }

    private static async Task<CredentialMaterialGenerationIdentifier> AcquireAsync(
        IEntraCredentialSession session,
        EntraCredentialAccessRequest request)
    {
        var inspection = await session.InspectCacheAsync(request, CancellationToken.None);
        using var gate = new SemaphoreSlim(1, 1);
        var context = new ProviderAcquisitionCommitContext(
            () => true, CancellationToken.None, gate, Permit(inspection.AcquisitionKey).BindExecution(() => true));
        return Assert.IsType<ProviderAcquisitionSucceeded>(
            await session.ExecuteAsync(request, context, CancellationToken.None)).CommittedGeneration;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntraCredentialSession_UnpinnedRequestCannotCommitAnotherAccountUnderRetainedAccountProof(
        bool deleteMaterial)
    {
        var request = Request();
        Assert.Null(request.Credential.AccountIdentifier);
        var acquirer = new Acquirer();
        var approvedAccount = acquirer.Account;
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var original = await AcquireAsync(session, request);
        if (deleteMaterial)
        {
            Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
                await session.InvalidateGenerationAsync(original, CancellationToken.None));
        }

        var before = await session.InspectGenerationAsync(CancellationToken.None);
        var inspection = await session.InspectCacheAsync(request, CancellationToken.None);
        Assert.Equal(approvedAccount, inspection.AcquisitionKey.Account);
        acquirer.Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "another-provider-account");
        using var gate = new SemaphoreSlim(1, 1);
        var context = new ProviderAcquisitionCommitContext(
            () => true,
            CancellationToken.None,
            gate,
            Permit(inspection.AcquisitionKey).BindExecution(() => true));
        var failure = Assert.IsType<ProviderAcquisitionFailed>(
            await session.ExecuteAsync(request, context, CancellationToken.None));
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Null(context.CommittedGeneration);
        Assert.Equal(before, await session.InspectGenerationAsync(CancellationToken.None));
        Assert.Equal(approvedAccount, (await session.InspectCacheAsync(request, CancellationToken.None)).AccountIdentifier);
        Assert.Equal(2, acquirer.Calls);
    }

    [Fact]
    public async Task EntraCredentialSession_ExportCopyRequiresFinalizedEntraReleaseAuthorization()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        Assert.Null(await session.CopyGenerationAsync(generation, null, null, CancellationToken.None));
        var connect = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        var authorized = EntraSessionTestAuthorization.Export(request, acquirer.Account);
        Assert.Null(await session.CopyGenerationAsync(
            generation, authorized.Authorization, connect.Context, CancellationToken.None));
        foreach (var validation in new[]
        {
            ReleaseAuthorizationValidation.AuthorizationRequired,
            ReleaseAuthorizationValidation.IdentityMismatch,
        })
        {
            var denied = EntraSessionTestAuthorization.Export(request, acquirer.Account, validation);
            Assert.Null(await session.CopyGenerationAsync(
                generation, denied.Authorization, denied.Context, CancellationToken.None));
        }

        using var material = await session.CopyGenerationAsync(
            generation, authorized.Authorization, authorized.Context, CancellationToken.None);
        Assert.NotNull(material);
        Assert.Equal("private-token", new string(material!.Token));
        Assert.Equal(1, acquirer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntraCredentialSession_PrivateUse_DisposesUnclaimedResultWhenProofEnds(bool asynchronous)
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var active = true;
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account, () => active);
        ReadOnlyMemory<char> observed = default;
        void AssertIdentityCleared() => Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        object result = asynchronous
            ? new TrackedAsynchronousResult { BeforeDisposal = AssertIdentityCleared }
            : new TrackedSynchronousResult { BeforeDisposal = AssertIdentityCleared };
        var outcome = await session.UseGenerationAsync(
            generation, capability.Context, capability.Permit, (material, _) =>
            {
                observed = material;
                active = false;
                return ValueTask.FromResult(result);
            }, CancellationToken.None);
        Assert.IsType<EntraGenerationUseDenied<object>>(outcome);
        if (result is TrackedAsynchronousResult asynchronousResult)
        {
            Assert.Equal(1, asynchronousResult.AsynchronousDisposals);
            Assert.Equal(0, asynchronousResult.SynchronousDisposals);
        }
        else
        {
            Assert.Equal(1, Assert.IsType<TrackedSynchronousResult>(result).Disposals);
        }

        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        Assert.True(await session.IsCurrentGenerationAsync(generation, CancellationToken.None));
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_SuccessTransfersResultWithoutDisposingIt()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        var result = new TrackedAsynchronousResult();
        var outcome = Assert.IsType<EntraGenerationUseSucceeded<TrackedAsynchronousResult>>(await session.UseGenerationAsync(
            generation, capability.Context, capability.Permit,
            (_, _) => ValueTask.FromResult(result), CancellationToken.None));
        Assert.Same(result, outcome.Result);
        Assert.Equal(0, result.AsynchronousDisposals);
        Assert.Equal(0, result.SynchronousDisposals);
        await outcome.Result.DisposeAsync();
        Assert.Equal(1, result.AsynchronousDisposals);
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_CancellationAfterCallbackDisposesUnclaimedResult()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        using var cancellation = new CancellationTokenSource();
        var result = new TrackedAsynchronousResult();
        ReadOnlyMemory<char> observed = default;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.UseGenerationAsync(
            generation, capability.Context, capability.Permit, (material, _) =>
            {
                observed = material;
                cancellation.Cancel();
                return ValueTask.FromResult(result);
            }, cancellation.Token));
        Assert.Equal(1, result.AsynchronousDisposals);
        Assert.Equal(0, result.SynchronousDisposals);
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_ResultCleanupFailurePropagatesAndReleasesIdentity()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var active = true;
        var capability = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account, () => active);
        var result = new TrackedAsynchronousResult { ThrowOnDisposal = true };
        ReadOnlyMemory<char> observed = default;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.UseGenerationAsync(
            generation, capability.Context, capability.Permit, (material, _) =>
            {
                observed = material;
                active = false;
                return ValueTask.FromResult(result);
            }, CancellationToken.None));
        Assert.Equal("Callback result cleanup failed.", failure.Message);
        Assert.Equal(1, result.AsynchronousDisposals);
        Assert.Equal(0, result.SynchronousDisposals);
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        var next = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        Assert.IsType<EntraGenerationUseSucceeded<int>>(await session.UseGenerationAsync(
            generation, next.Context, next.Permit,
            (material, _) => ValueTask.FromResult(material.Length), CancellationToken.None));
    }

    [Fact]
    public async Task EntraCredentialSession_PrivateUse_PostCallbackValidationThrowDisposesUnclaimedResult()
    {
        var request = Request();
        var acquirer = new Acquirer();
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer);
        var generation = await AcquireAsync(session, request);
        var returned = false;
        var capability = EntraSessionTestAuthorization.PrivateConnect(
            request, acquirer.Account, acquisitionValidation: () => returned
                ? throw new InvalidOperationException("Post-callback validation failed.")
                : ReleaseAuthorizationValidation.Valid);
        ReadOnlyMemory<char> observed = default;
        var result = new TrackedAsynchronousResult
        {
            BeforeDisposal = () => Assert.All(observed.ToArray(), character => Assert.Equal('\0', character)),
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.UseGenerationAsync(
            generation, capability.Context, capability.Permit, (material, _) =>
            {
                observed = material;
                returned = true;
                return ValueTask.FromResult(result);
            }, CancellationToken.None));
        Assert.Equal("Post-callback validation failed.", failure.Message);
        Assert.Equal(1, result.AsynchronousDisposals);
        Assert.Equal(0, result.SynchronousDisposals);
        Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        Assert.True(await session.IsCurrentGenerationAsync(generation, CancellationToken.None));
        var next = EntraSessionTestAuthorization.PrivateConnect(request, acquirer.Account);
        Assert.IsType<EntraGenerationUseSucceeded<int>>(await session.UseGenerationAsync(
            generation, next.Context, next.Permit,
            (material, _) => ValueTask.FromResult(material.Length), CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EntraCredentialSession_PrivateUse_ExpiryDuringFinalValidationPreventsTransfer(
        bool afterCallback,
        bool asynchronousResult)
    {
        var request = Request();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var acquirer = new Acquirer { ExpiresOn = clock.GetUtcNow().AddHours(1) };
        await using var session = new EntraCredentialSession(request, new ForbiddenCredential(), acquirer, clock);
        var generation = await AcquireAsync(session, request);
        var validationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validationCalls = 0;
        var capability = EntraSessionTestAuthorization.PrivateConnect(
            request, acquirer.Account, asynchronousValidationCheckpoint: async () =>
            {
                if (++validationCalls == (afterCallback ? 5 : 4))
                {
                    validationEntered.SetResult();
                    await resumeValidation.Task;
                }
            });
        ReadOnlyMemory<char> observed = default;
        void AssertIdentityCleared() => Assert.All(observed.ToArray(), character => Assert.Equal('\0', character));
        object result = asynchronousResult
            ? new TrackedAsynchronousResult { BeforeDisposal = AssertIdentityCleared }
            : new TrackedSynchronousResult { BeforeDisposal = AssertIdentityCleared };
        var callbacks = 0;
        var use = session.UseGenerationAsync(
            generation, capability.Context, capability.Permit, (material, _) =>
            {
                callbacks++;
                observed = material;
                return ValueTask.FromResult(result);
            }, CancellationToken.None).AsTask();
        try
        {
            await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromHours(1));
        }
        finally
        {
            resumeValidation.TrySetResult();
        }

        Assert.IsType<EntraGenerationUseUnavailable<object>>(await use);
        Assert.Equal(afterCallback ? 1 : 0, callbacks);
        if (result is TrackedAsynchronousResult asynchronous)
        {
            Assert.Equal(afterCallback ? 1 : 0, asynchronous.AsynchronousDisposals);
            Assert.Equal(0, asynchronous.SynchronousDisposals);
        }
        else
        {
            Assert.Equal(afterCallback ? 1 : 0, Assert.IsType<TrackedSynchronousResult>(result).Disposals);
        }

        AssertIdentityCleared();
        Assert.Equal(1, acquirer.Calls);
    }

    private sealed class TrackedAsynchronousResult : IAsyncDisposable, IDisposable
    {
        internal int AsynchronousDisposals { get; private set; }
        internal int SynchronousDisposals { get; private set; }
        internal bool ThrowOnDisposal { get; init; }
        internal Action? BeforeDisposal { get; init; }

        public ValueTask DisposeAsync()
        {
            this.AsynchronousDisposals++;
            this.BeforeDisposal?.Invoke();
            if (this.ThrowOnDisposal)
            {
                throw new InvalidOperationException("Callback result cleanup failed.");
            }

            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            this.SynchronousDisposals++;
            this.BeforeDisposal?.Invoke();
        }
    }

    private sealed class TrackedSynchronousResult : IDisposable
    {
        internal int Disposals { get; private set; }
        internal Action? BeforeDisposal { get; init; }
        public void Dispose()
        {
            this.Disposals++;
            this.BeforeDisposal?.Invoke();
        }
    }

    internal static EntraCredentialAccessRequest Request()
    {
        var requestor = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "test");
        var lineage = new CredentialUseLineage();
        var request = new EntraCredentialAccessRequest
        {
            Authority = new() { Value = new Uri("https://login.microsoftonline.com/organizations/v2.0") },
            Client = new() { Value = "test-client" },
            CachePartition = new() { Value = "test-partition" },
            Scopes = [new() { Value = "api://example/.default" }],
            Credential = new()
            {
                LogicalIdentifier = new() { Digest = new string('a', 64) },
                Provider = CredentialProviderKind.Entra,
                Kind = CredentialKind.AccessToken,
            },
            Requestor = new() { Identifier = requestor, Kind = AccessRequestorKind.Application, SafeDisplayName = "Application" },
            Use = new()
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Application, lineage, requestor, "test"),
                Kind = CredentialUseKind.Application,
                Lineage = lineage,
                SafeDisplayName = "Test",
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.AllUses],
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = [],
        };
        return request with
        {
            Credential = request.Credential with { LogicalIdentifier = request.CreateLogicalIdentifier() },
            Sources = [new()
            {
                Identifier = request.CreateSourceIdentifier(),
                Kind = CredentialSourceKind.ProviderCache,
                Provider = CredentialProviderKind.Entra,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = "Microsoft Entra",
            }],
        };
    }

    internal sealed class Acquirer : IEntraTokenAcquirer
    {
        internal int Calls { get; private set; }
        internal Exception? Failure { get; set; }
        internal DateTimeOffset ExpiresOn { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
        internal CredentialAccountIdentifier Account { get; set; } =
            CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "stable-test-account");
        internal Func<CancellationToken, ValueTask>? BeforeAcquire { get; set; }

        public async ValueTask<EntraAcquiredToken> AcquireAsync(CancellationToken cancellationToken)
        {
            this.Calls++;
            if (this.BeforeAcquire is not null)
            {
                await this.BeforeAcquire(cancellationToken);
            }

            if (this.Failure is not null)
            {
                throw this.Failure;
            }

            return new() { Token = new("private-token", this.ExpiresOn), AccountIdentifier = this.Account };
        }
    }

    internal sealed class ForbiddenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected Azure Identity call.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected Azure Identity call.");
    }
}
