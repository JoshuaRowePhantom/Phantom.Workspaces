using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubPrivateCredentialSessionTests
{
    public static TheoryData<bool, int, bool, bool> GenerationBoundaryCases
    {
        get
        {
            var cases = new TheoryData<bool, int, bool, bool>();
            foreach (var afterCallback in new[] { false, true })
            foreach (var change in new[] { 0, 1, 2 })
            foreach (var knownAccount in new[] { false, true })
            foreach (var asynchronousResult in new[] { false, true })
            {
                cases.Add(afterCallback, change, knownAccount, asynchronousResult);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(GenerationBoundaryCases))]
    public Task PrivateMaterialUse_FinalPolicyAwaitRechecksExactUsableGeneration(
        bool afterCallback,
        int change,
        bool knownAccount,
        bool asynchronousResult) =>
        VerifyGenerationBoundaryAsync(afterCallback, change, knownAccount, asynchronousResult, false);

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public Task PrivateMaterialUse_StaleResultCleanupFaultIsRetained(
        int change,
        bool asynchronousResult) =>
        VerifyGenerationBoundaryAsync(true, change, true, asynchronousResult, true);

    private static async Task VerifyGenerationBoundaryAsync(
        bool afterCallback,
        int change,
        bool knownAccount,
        bool asynchronousResult,
        bool cleanupThrows)
    {
        var events = new List<string>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new Source(events) { ExpiresOn = clock.GetUtcNow().AddHours(1) };
        var provider = new GitHubCredentialAccessExecutor(source, clock);
        var parent = new ParentExecutor(provider, events)
        {
            IdentityRequest = new()
            {
                Source = CredentialSourceKind.GitHubCli,
                AccountIdentifier = knownAccount ? source.Account : null,
            },
        };
        var path = System.IO.Path.Combine(
            Environment.CurrentDirectory, ".access-private-isolated-policy", Guid.NewGuid().ToString("N"), "memory.json");
        var policy = new PausingAcquisitionPolicy(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)));
        var (broker, registry) = Create(provider, parent, policy);
        await using var owned = broker;
        var callbackCalls = 0;
        var cleanupCalls = 0;
        char[]? borrowedMaterial = null;
        var cleanupFault = new InvalidOperationException("Derived result cleanup failed.");
        void Cleanup()
        {
            cleanupCalls++;
            Assert.NotNull(borrowedMaterial);
            Assert.All(borrowedMaterial, character => Assert.Equal('\0', character));
            if (cleanupThrows)
            {
                throw cleanupFault;
            }
        }
        var asynchronous = new BoundaryAsyncResult(Cleanup);
        var synchronous = new BoundarySyncResult(Cleanup);
        object derived = asynchronousResult ? asynchronous : synchronous;
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.BodyOverride = async (context, commit, cancellationToken) =>
        {
            var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, cancellationToken);
            var acquisition = provider.CreatePrivateAcquisition(parent.IdentityRequest, inspection.Source, context);
            var acquired = Assert.IsType<ProviderAcquisitionSucceeded>(
                await context.ExecutePrivateProviderDependencyAsync(acquisition, commit.InteractionPermit, cancellationToken));
            var cache = provider.GetGenerationCache(inspection.Source.Identifier);
            var beforeCallback = policy.Arm(3);
            PolicyBarrier? afterCallbackBarrier = null;
            var use = provider.UseGenerationAsync(
                inspection.Source, acquired.CommittedGeneration, context, commit.InteractionPermit,
                (material, account, _) =>
                {
                    callbackCalls++;
                    Assert.Equal("identity-material", material);
                    Assert.Equal(source.Account, account);
                    Assert.True(afterCallback, "Stale identity must not reach the callback.");
                    afterCallbackBarrier = policy.Arm(1);
                    callbackEntered.SetResult();
                    return ValueTask.FromResult(derived);
                }, cancellationToken).AsTask();
            await beforeCallback.Reached.Task.WaitAsync(cancellationToken);
            Assert.False(use.IsCompleted);
            Assert.Equal(ReleaseAuthorizationValidation.Valid, beforeCallback.Validation);
            // Observe the independent borrowed buffer without adding a production test hook.
            var stateMachine = use.GetType().GetField("StateMachine")!.GetValue(use)!;
            var snapshot = stateMachine.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(field => field.FieldType.Name == "Snapshot").GetValue(stateMachine)!;
            borrowedMaterial = Assert.IsType<char[]>(snapshot.GetType().GetProperty("Material")!.GetValue(snapshot));
            Assert.Equal("identity-material", new string(borrowedMaterial));
            var boundary = beforeCallback;
            if (afterCallback)
            {
                beforeCallback.Continue.SetResult();
                // The callback arms the next actual policy await before signalling completion.
                await callbackEntered.Task.WaitAsync(cancellationToken);
                boundary = Assert.IsType<PolicyBarrier>(afterCallbackBarrier);
                await boundary.Reached.Task.WaitAsync(cancellationToken);
                Assert.Equal(1, callbackCalls);
                Assert.All(borrowedMaterial, character => Assert.Equal('\0', character));
                Assert.Equal(0, cleanupCalls);
            }
            Assert.False(use.IsCompleted);
            Assert.Equal(ReleaseAuthorizationValidation.Valid, boundary.Validation);
            CredentialMaterialGenerationIdentifier? replacement = null;
            try
            {
                if (change == 2)
                {
                    clock.SetUtcNow(source.ExpiresOn!.Value);
                    Assert.Equal(source.ExpiresOn, clock.GetUtcNow());
                }
                else
                {
                    Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
                        await cache.InvalidateGenerationAsync(acquired.CommittedGeneration, cancellationToken));
                    if (change == 1)
                    {
                        source.Material = "replacement-identity-material";
                        var rotation = broker.AcquireAsync(
                            GitHubCredentialAccessExecutorTests.Request(CredentialSourceKind.GitHubCli), cancellationToken);
                        await AllowAsync(registry);
                        await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await rotation).Result.DisposeAsync();
                        replacement = (await cache.InspectGenerationAsync(cancellationToken))!.Generation;
                        Assert.NotEqual(acquired.CommittedGeneration, replacement);
                    }
                }
            }
            finally
            {
                boundary.Continue.TrySetResult();
            }
            if (cleanupThrows)
            {
                Assert.Same(cleanupFault, await Assert.ThrowsAsync<InvalidOperationException>(() => use));
            }
            else
            {
                var failure = Assert.IsType<GitHubPrivateCredentialUseFailed<object>>(await use);
                Assert.Equal(AccessErrorCode.InvalidGeneration, failure.ErrorCode);
                Assert.True(failure.CanRetry);
            }
            Assert.Equal(afterCallback ? 1 : 0, callbackCalls);
            Assert.Equal(afterCallback ? 1 : 0, cleanupCalls);
            Assert.Equal(afterCallback && asynchronousResult ? 1 : 0, asynchronous.AsyncDisposals);
            Assert.Equal(0, asynchronous.SyncDisposals);
            Assert.Equal(afterCallback && !asynchronousResult ? 1 : 0, synchronous.Disposals);
            Assert.All(borrowedMaterial, character => Assert.Equal('\0', character));
            Assert.Equal(change == 1 ? 2 : 1, source.Acquisitions);
            Assert.Equal(replacement, (await cache.InspectGenerationAsync(cancellationToken))?.Generation);
            Assert.Equal(source.Account, (await provider.InspectPrivateAsync(parent.IdentityRequest, cancellationToken)).Source.RetainedAccountIdentifier);
            // The genuine, still-active Connect proof remains valid despite the GitHub rejection.
            Assert.True(await commit.InteractionPermit.AuthorizesPrivateUseAsync(context, cancellationToken));
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
        };
        var request = Request();
        if (knownAccount)
        {
            request = request with { Credential = request.Credential with { AccountIdentifier = source.Account } };
        }
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.InvalidGeneration, Assert.IsType<AccessFailed<ParentResult>>(await task).ErrorCode);
        Assert.DoesNotContain("connect committed", events);
    }

    [Fact]
    public async Task PrivateMaterialUse_SuccessTransfersDisposableCallbackResultToCaller()
    {
        var events = new List<string>();
        var provider = new GitHubCredentialAccessExecutor(new Source(events));
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var result = new DerivedResult();
        var transferred = false;
        parent.BodyOverride = async (context, commit, cancellationToken) =>
        {
            var used = await provider.UsePrivateAsync(
                parent.IdentityRequest, context, commit.InteractionPermit,
                (_, _) => ValueTask.FromResult(result), cancellationToken);
            Assert.Same(result, Assert.IsType<GitHubPrivateCredentialUseSucceeded<DerivedResult>>(used).Result);
            Assert.Equal(0, result.AsyncDisposals);
            Assert.Equal(0, result.SyncDisposals);
            transferred = true;
            await result.DisposeAsync();
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
        };
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.InvalidGeneration, Assert.IsType<AccessFailed<ParentResult>>(await task).ErrorCode);
        Assert.True(transferred);
        Assert.Equal(1, result.AsyncDisposals);
        Assert.Equal(0, result.SyncDisposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateAcquisition_RealUnconsumedParentAdmissionCannotStartChildProvider(bool knownAccount)
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events)
        {
            UseSplitBoundary = true,
            IdentityRequest = new()
            {
                Source = CredentialSourceKind.GitHubCli,
                AccountIdentifier = knownAccount ? source.Account : null,
            },
        };
        parent.BeforeParentAcquisition = async (context, rawPermit, cancellationToken) =>
        {
            var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, cancellationToken);
            var acquisition = provider.CreatePrivateAcquisition(parent.IdentityRequest, inspection.Source, context);
            Assert.IsType<ProviderAcquisitionDenied>(
                await context.ExecutePrivateProviderDependencyAsync(acquisition, rawPermit, cancellationToken));
            Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(await provider.UsePrivateAsync<int>(
                parent.IdentityRequest, context, rawPermit,
                (_, _) => throw new InvalidOperationException("Raw admission must not reach a private callback."),
                cancellationToken));
            Assert.Equal(0, source.Acquisitions);
        };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var request = Request();
        request = request with { Credential = request.Credential with { AccountIdentifier = knownAccount ? source.Account : null } };
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(1, source.Acquisitions);
        Assert.Equal(1, events.Count(item => item == "mint"));
    }

    [Fact]
    public async Task PrivateMaterialUse_CancelledCallbackResultIsDisposedBeforeReturning()
    {
        var events = new List<string>();
        var provider = new GitHubCredentialAccessExecutor(new Source(events));
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var result = new DerivedResult();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.BodyOverride = async (context, commit, groupCancellation) =>
        {
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await provider.UsePrivateAsync(
                        parent.IdentityRequest, context, commit.InteractionPermit,
                        async (_, callbackCancellation) =>
                        {
                            var groupCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            using var registration = callbackCancellation.Register(() => groupCancelled.TrySetResult());
                            cancellation.Cancel();
                            await groupCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
                            return result;
                        }, groupCancellation));
                Assert.Equal(1, result.AsyncDisposals);
                Assert.Equal(0, result.SyncDisposals);
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
            return new ProviderAcquisitionCancelled();
        };
        var task = broker.AcquireAsync(Request(), cancellation.Token);
        await AllowAsync(registry);
        await task;
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrivateMaterialUse_RejectedResultCleanupIsJoinedAndFaultsPropagate(
        bool asynchronous, bool cleanupFails)
    {
        var events = new List<string>();
        var provider = new GitHubCredentialAccessExecutor(new Source(events));
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("Derived callback cleanup failed.");
        IDisposable result = asynchronous
            ? new DerivedResult
            {
                OnAsyncDispose = async () =>
                {
                    cleanupStarted.SetResult();
                    await allowCleanup.Task.WaitAsync(TestContext.Current.CancellationToken);
                    if (cleanupFails)
                    {
                        throw cleanupFailure;
                    }
                },
            }
            : new SynchronousDerivedResult
            {
                OnDispose = () =>
                {
                    cleanupStarted.SetResult();
                    if (cleanupFails)
                    {
                        throw cleanupFailure;
                    }
                },
            };
        parent.BodyOverride = async (context, commit, groupCancellation) =>
        {
            try
            {
                var error = await Record.ExceptionAsync(async () =>
                {
                    await provider.UsePrivateAsync(
                        parent.IdentityRequest, context, commit.InteractionPermit,
                        async (_, callbackCancellation) =>
                        {
                            var groupCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            using var registration = callbackCancellation.Register(() => groupCancelled.TrySetResult());
                            cancellation.Cancel();
                            await groupCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
                            return result;
                        }, groupCancellation);
                });
                if (cleanupFails)
                {
                    Assert.Same(cleanupFailure, error);
                }
                else
                {
                    Assert.IsAssignableFrom<OperationCanceledException>(error);
                }
                AssertDisposals(result, asynchronous ? 1 : 0, asynchronous ? 0 : 1);
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
            return new ProviderAcquisitionCancelled();
        };
        var pending = broker.AcquireAsync(Request(), cancellation.Token);
        await AllowAsync(registry);
        try
        {
            await cleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (asynchronous)
            {
                Assert.False(completed.Task.IsCompleted);
            }
            AssertDisposals(result, asynchronous ? 1 : 0, asynchronous ? 0 : 1);
        }
        finally
        {
            allowCleanup.TrySetResult();
        }
        Assert.IsType<AccessCancelled<ParentResult>>(await pending);
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateMaterialUse_SuccessTransfersResultOwnershipToCaller(bool asynchronous)
    {
        var events = new List<string>();
        var provider = new GitHubCredentialAccessExecutor(new Source(events));
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        IDisposable result = asynchronous ? new DerivedResult() : new SynchronousDerivedResult();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.BodyOverride = async (context, commit, cancellationToken) =>
        {
            try
            {
                var success = Assert.IsType<GitHubPrivateCredentialUseSucceeded<IDisposable>>(
                    await provider.UsePrivateAsync(
                        parent.IdentityRequest, context, commit.InteractionPermit,
                        (_, _) => ValueTask.FromResult(result), cancellationToken));
                Assert.Same(result, success.Result);
                using (success.Result)
                {
                    AssertDisposals(result, 0, 0);
                }
                AssertDisposals(result, 0, 1);
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        };
        var pending = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.ProviderFailure, Assert.IsType<AccessFailed<ParentResult>>(await pending).ErrorCode);
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        AssertDisposals(result, 0, 1);
    }

    private static void AssertDisposals(IDisposable result, int asynchronous, int synchronous)
    {
        Assert.Equal(asynchronous, (result as DerivedResult)?.AsyncDisposals ?? 0);
        Assert.Equal(synchronous, result is DerivedResult derived
            ? derived.SyncDisposals : Assert.IsType<SynchronousDerivedResult>(result).Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task PrivateMaterialUse_RejectsExpiredRawWrongExecutorAndWrongKeyProofs(int proofKind)
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        var context = parent.Context!;
        var key = new ProviderAcquisitionKey
        {
            Configuration = context.PolicyRequest!.Credential.LogicalIdentifier,
            Source = context.PolicyRequest.SourceIdentifier,
            Account = context.PolicyRequest.Credential.AccountIdentifier,
            Protocol = ProviderProtocolKind.TunnelConnect,
        };
        var proof = parent.Permit!;
        if (proofKind != 0)
        {
            var candidate = new ProviderInteractionPermit(
                context.RequestIdentifier, context.Revision,
                proofKind == 2 ? CredentialExecutorKind.GitHub : CredentialExecutorKind.DevTunnelConnect,
                context.AcquisitionGeneration,
                proofKind == 3 ? key with { Configuration = new() { Digest = new string('e', 64) } } : key,
                () => true);
            proof = proofKind == 1 ? candidate : candidate.BindExecution(() => true, TestContext.Current.CancellationToken);
        }
        if (proofKind == 5)
        {
            proof = null!;
        }
        if (proofKind == 6)
        {
            context = null!;
        }
        Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(await provider.UseGenerationAsync<int>(
            inspection.Source, inspection.CachedGeneration!.Generation, context, proof,
            (_, _, _) => throw new InvalidOperationException("An invalid parent proof must not expose identity material."),
            TestContext.Current.CancellationToken));
        Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(await provider.UsePrivateAsync<int>(
            parent.IdentityRequest, context, proof,
            (_, _) => throw new InvalidOperationException("The combined route must require the same consumed parent proof."),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Acquisitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateAcquisition_StaleOriginallyUnpinnedHandleCannotEraseRetainedPrincipal(bool deleteMaterial)
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var identityRequest = new GitHubPrivateCredentialRequest { Source = CredentialSourceKind.GitHubCli };
        var unpinned = await provider.InspectPrivateAsync(identityRequest, TestContext.Current.CancellationToken);
        Assert.Null(unpinned.Source.ExpectedAccountIdentifier);
        Assert.Null(unpinned.Source.RetainedAccountIdentifier);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var first = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await first).Result.DisposeAsync();
        var accountA = source.Account;
        var cache = provider.GetGenerationCache(unpinned.Source.Identifier);
        var generation = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        if (deleteMaterial)
        {
            await cache.InvalidateGenerationAsync(generation, TestContext.Current.CancellationToken);
        }
        source.Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "stale-handle-principal-B");
        parent.BodyOverride = (context, _, _) =>
        {
            Assert.Throws<ReleaseIdentityMismatchException>(() => provider.CreatePrivateAcquisition(identityRequest, unpinned.Source, context));
            return ValueTask.FromResult<ProviderAcquisitionOutcome>(new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch });
        };
        var second = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<ParentResult>>(await second).ErrorCode);
        Assert.Equal(1, source.Acquisitions);
        var retainedGeneration = await cache.InspectGenerationAsync(TestContext.Current.CancellationToken);
        if (deleteMaterial)
        {
            Assert.Null(retainedGeneration);
        }
        else
        {
            Assert.Equal(generation, retainedGeneration!.Generation);
            Assert.Equal(accountA, retainedGeneration.AccountIdentifier);
        }
        Assert.Equal(accountA, (await provider.InspectPrivateAsync(identityRequest, TestContext.Current.CancellationToken)).Source.ExpectedAccountIdentifier);
    }

    [Fact]
    public async Task PrivateAcquisition_PrincipalChangesWhileProviderRuns_CannotCommitUsingUnpinnedAdmittedKey()
    {
        var events = new List<string>();
        var source = new Source(events)
        {
            Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "private-inflight-principal-B"),
        };
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "concurrent-approved-principal-A");
        source.BeforeReturn = async cancellationToken =>
        {
            source.BeforeReturn = null;
            source.Account = accountA;
            var request = GitHubCredentialAccessExecutorTests.Request(CredentialSourceKind.GitHubCli);
            var accepted = broker.AcquireAsync(request with
            {
                Credential = request.Credential with { AccountIdentifier = accountA },
            }, cancellationToken);
            await AllowAsync(registry);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await accepted).Result.DisposeAsync();
        };
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<ParentResult>>(await task).ErrorCode);
        Assert.Null(parent.UsedAccount);
        Assert.DoesNotContain("mint", events);
        var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        Assert.Equal(accountA, inspection.Source.ExpectedAccountIdentifier);
        Assert.Equal(accountA, inspection.CachedGeneration!.AccountIdentifier);
        Assert.Equal(2, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_MetadataIsSafeAndRealPrivateCoordinatorProofCommitsOneGeneration()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var metadata = await provider.InspectPrivateAsync(
            new GitHubPrivateCredentialRequest { Source = CredentialSourceKind.GitHubCli },
            TestContext.Current.CancellationToken);
        Assert.Null(metadata.CachedGeneration);
        Assert.Equal(CredentialSourceUsability.AcquisitionRequired, metadata.Usability);
        Assert.Equal(metadata.Source.Identifier, metadata.AcquisitionKey.Source);
        Assert.Equal(GitHubCredentialAccessRequest.CreateLogicalIdentifier(), metadata.AcquisitionKey.Configuration);
        Assert.Equal(0, source.Acquisitions);
        Assert.Empty(events);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Empty(events);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(new[] { "release validated", "identity acquired", "mint", "connect committed" }, events);
        Assert.Equal(source.Account, parent.UsedAccount);
        var cached = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        Assert.Equal(parent.UsedGeneration, cached.CachedGeneration!.Generation);
        Assert.Equal(parent.UsedAccount, cached.Source.ExpectedAccountIdentifier);
        Assert.Equal(CredentialSourceUsability.Usable, cached.Usability);
        Assert.Single(registry.GetSnapshot());
        Assert.DoesNotContain("identity-material", System.Text.Json.JsonSerializer.Serialize(cached));
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_ExactGenerationCannotSubstituteRotatedAccountBeforeMint()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "split-principal-B");
        parent.BeforeGenerationUse = async (inspection, generation, cancellationToken) =>
        {
            var cache = provider.GetGenerationCache(inspection.Source.Identifier);
            Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
                await cache.InvalidateGenerationAsync(generation, cancellationToken));
            source.Account = accountB;
            source.Material = "identity-material-B";
            var request = GitHubCredentialAccessExecutorTests.Request(CredentialSourceKind.GitHubCli);
            var rotated = broker.AcquireAsync(request with
            {
                Credential = request.Credential with { AccountIdentifier = accountB },
            }, cancellationToken);
            await AllowAsync(registry);
            await Assert.IsType<AccessSucceeded<GitHubCredentialAccessResult>>(await rotated).Result.DisposeAsync();
            var current = (await cache.InspectGenerationAsync(cancellationToken))!;
            Assert.NotEqual(generation, current.Generation);
            Assert.Equal(accountB, current.AccountIdentifier);
        };
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        var failure = Assert.IsType<AccessFailed<ParentResult>>(await task);
        Assert.Equal(AccessErrorCode.InvalidGeneration, failure.ErrorCode);
        Assert.Null(parent.UsedAccount);
        Assert.DoesNotContain("mint", events);
        Assert.DoesNotContain("connect committed", events);
        Assert.Equal(2, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_AccountPinMismatchCannotUseCachedGenerationOrRefresh()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        parent.BeforeGenerationUse = async (_, generation, cancellationToken) =>
        {
            var pinB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "different-private-principal");
            var inspection = await provider.InspectPrivateAsync(new GitHubPrivateCredentialRequest
            {
                Source = CredentialSourceKind.GitHubCli,
                AccountIdentifier = pinB,
            }, cancellationToken);
            Assert.Equal(CredentialSourceUsability.AcquisitionRequired, inspection.Usability);
            Assert.Equal(pinB, inspection.AcquisitionKey.Account);
            Assert.Equal(source.Account, inspection.CachedGeneration!.AccountIdentifier);
            var outcome = await provider.UseGenerationAsync<int>(
                inspection.Source, generation, parent.Context!, parent.Permit!,
                (_, _, _) => throw new InvalidOperationException("A mismatched principal must not reach the operation."),
                cancellationToken);
            Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<GitHubPrivateCredentialUseFailed<int>>(outcome).ErrorCode);
        };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_CallbackFailureDoesNotDisposeProviderCacheOrRefresh()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        parent.BeforeGenerationUse = async (inspection, generation, cancellationToken) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UseGenerationAsync<int>(
                inspection.Source, generation, parent.Context!, parent.Permit!,
                (_, _, _) => ValueTask.FromException<int>(new InvalidOperationException("Private operation failed.")),
                cancellationToken).AsTask());
            var next = await provider.UseGenerationAsync(
                inspection.Source, generation, parent.Context!, parent.Permit!, (_, account, _) =>
                {
                    Assert.Equal(source.Account, account);
                    return ValueTask.FromResult(1);
                }, cancellationToken);
            Assert.Equal(1, Assert.IsType<GitHubPrivateCredentialUseSucceeded<int>>(next).Result);
        };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task SplitPrivateIdentity_UnconsumedProofCannotExecuteProviderAcquisition()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events) { UseSplitBoundary = true };
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, TestContext.Current.CancellationToken);
        var operation = provider.CreatePrivateAcquisition(parent.IdentityRequest, inspection.Source, parent.Context!);
        Assert.Equal(inspection.AcquisitionKey, operation.CompatibilityKey);
        var unconsumed = new ProviderInteractionPermit(
            operation.RequestIdentifier, operation.Revision, CredentialExecutorKind.GitHub,
            operation.AcquisitionGeneration, operation.CompatibilityKey, () => true);
        using var gate = new SemaphoreSlim(1, 1);
        var commit = new ProviderAcquisitionCommitContext(() => true, TestContext.Current.CancellationToken, gate, unconsumed);
        Assert.IsType<ProviderAcquisitionDenied>(await operation.ExecuteAsync(commit, TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_ConnectReleasePrecedesIdentityAndMint_WithoutIdentityExportRow()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Empty(events);
        Assert.Equal(0, source.Acquisitions);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        Assert.Equal(new[] { "release validated", "identity acquired", "mint", "connect committed" }, events);
        Assert.Single(registry.GetSnapshot());
        Assert.Single(await provider.InspectCachedGenerationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await broker.Policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("identity-material", System.Text.Json.JsonSerializer.Serialize(registry.GetSnapshot()));
    }

    [Fact]
    public async Task PrivateGitHubIdentity_CachedReuseStillRequiresIndependentConnectRelease()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var first = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await first).Result.DisposeAsync();
        events.Clear();
        var second = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Empty(events);
        Assert.False(second.IsCompleted);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await second).Result.DisposeAsync();
        Assert.Equal(new[] { "release validated", "mint", "connect committed" }, events);
        Assert.Equal(1, source.Acquisitions);
        Assert.Equal(2, registry.GetSnapshot().Count);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_DeniedConnect_DoesNotAcquireIdentityOrMint()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(provider, new ParentExecutor(provider, events));
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<ParentResult>>(await task);
        Assert.Empty(events);
        Assert.Equal(0, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_ExpiredParentProof_CannotUseCachedMaterial()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var parent = new ParentExecutor(provider, events);
        var (broker, registry) = Create(provider, parent);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        await Assert.IsType<AccessSucceeded<ParentResult>>(await task).Result.DisposeAsync();
        var outcome = await provider.UsePrivateAsync<int>(
            new GitHubPrivateCredentialRequest { Source = CredentialSourceKind.GitHubCli },
            parent.Context!,
            parent.Permit!,
            (_, _) => throw new InvalidOperationException("Expired proof must not expose private material."),
            TestContext.Current.CancellationToken);
        Assert.IsType<GitHubPrivateCredentialUseDenied<int>>(outcome);
        Assert.Equal(1, source.Acquisitions);
    }

    [Fact]
    public async Task PrivateGitHubIdentity_ExportedIdentityContext_CannotUsePrivateConnectRoute()
    {
        var events = new List<string>();
        var source = new Source(events);
        var provider = new GitHubCredentialAccessExecutor(source);
        var (broker, registry) = Create(provider, new ParentExecutor(provider, events));
        await using var owned = broker;
        var request = Request();
        var task = broker.AcquireAsync(request with
        {
            Credential = request.Credential with { Kind = CredentialKind.IdentityToken },
        }, TestContext.Current.CancellationToken);
        await AllowAsync(registry);
        Assert.IsType<AccessDenied<ParentResult>>(await task);
        Assert.Equal(new[] { "release validated" }, events);
        Assert.Equal(0, source.Acquisitions);
    }

    private static ParentRequest Request()
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "private-connect-test"),
            Kind = AccessRequestorKind.Application,
            SafeDisplayName = "Tunnel",
        };
        var lineage = new CredentialUseLineage();
        return new ParentRequest
        {
            Requestor = requestor,
            Use = new CredentialUseDescriptor
            {
                Kind = CredentialUseKind.Tunnel,
                Lineage = lineage,
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Tunnel, lineage, requestor.Identifier, "connect"),
                SafeDisplayName = "Connect tunnel",
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Credential = new CredentialReference
            {
                LogicalIdentifier = new() { Digest = new string('c', 64) },
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
            },
            Sources =
            [
                new CredentialSourceChoiceDescriptor
                {
                    Identifier = new() { Digest = new string('d', 64) },
                    Kind = CredentialSourceKind.Provider,
                    Provider = CredentialProviderKind.DevTunnel,
                    Usability = CredentialSourceUsability.AcquisitionRequired,
                    SafeDisplayName = "Tunnel provider",
                },
            ],
        };
    }

    private static (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(
        GitHubCredentialAccessExecutor provider,
        ParentExecutor parent,
        IReleaseAuthorizationPolicy? policy = null)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(provider);
        executors.Add<ParentRequest, ParentResult>(parent);
        var path = System.IO.Path.Combine(
            Environment.CurrentDirectory, ".access-private-isolated-policy", Guid.NewGuid().ToString("N"), "memory.json");
        return (new AccessBroker(policy ?? new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(path)), executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    private static async Task AllowAsync(UserAuthorizationRegistry registry)
    {
        var row = registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SelectedSource,
        }, TestContext.Current.CancellationToken);
    }

    private sealed record ParentRequest : CredentialAccessRequest<ParentResult>;

    private sealed class ParentResult : ICredentialAccessResult
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ParentExecutor(
        GitHubCredentialAccessExecutor provider,
        List<string> events) : ICredentialAccessExecutor<ParentRequest, ParentResult>
    {
        private string? derivedMaterial;
        public ICredentialExecutionContext? Context { get; private set; }
        public ProviderInteractionPermit? Permit { get; private set; }
        public bool UseSplitBoundary { get; init; }
        public GitHubPrivateCredentialRequest IdentityRequest { get; init; } = new() { Source = CredentialSourceKind.GitHubCli };
        public CredentialAccountIdentifier? UsedAccount { get; private set; }
        public CredentialMaterialGenerationIdentifier? UsedGeneration { get; private set; }
        public Func<GitHubPrivateCredentialInspection, CredentialMaterialGenerationIdentifier, CancellationToken, ValueTask>? BeforeGenerationUse { get; set; }
        public Func<ICredentialExecutionContext, ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>>? BodyOverride { get; set; }
        public Func<ICredentialExecutionContext, ProviderInteractionPermit, CancellationToken, ValueTask>? BeforeParentAcquisition { get; set; }
        public CredentialExecutorKind Kind => CredentialExecutorKind.DevTunnelConnect;

        public ValueTask<CredentialInspection> InspectAsync(ParentRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential,
            Sources = request.Sources,
            AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
            {
                [request.Sources[0].Identifier] = new()
                {
                    Configuration = request.Credential.LogicalIdentifier,
                    Source = request.Sources[0].Identifier,
                    Account = request.Credential.AccountIdentifier,
                    Protocol = ProviderProtocolKind.TunnelConnect,
                },
            },
        });

        public async ValueTask<CredentialExecutionOutcome<ParentResult>> ExecuteAsync(
            ParentRequest request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(request.Credential.AccountIdentifier is null ? ReleaseAuthorizationValidation.IdentityMismatch : ReleaseAuthorizationValidation.Valid,
                await context.Policy.ValidateAsync(authorization.Identifier, context.PolicyRequest!, cancellationToken));
            events.Add("release validated");
            Assert.NotNull(interactionPermit);
            if (this.BeforeParentAcquisition is { } beforeParent)
            {
                await beforeParent(context, interactionPermit, cancellationToken);
            }
            var operation = new Operation(this, provider, events, context, inspection.AcquisitionKeys[inspection.SelectedSource!]);
            var acquired = await context.ExecuteProviderAcquisitionAsync(operation, interactionPermit, cancellationToken);
            if (acquired is ProviderAcquisitionDenied)
            {
                return new CredentialExecutionDenied<ParentResult>();
            }
            if (acquired is ProviderAcquisitionFailed failed)
            {
                return new CredentialExecutionFailed<ParentResult> { ErrorCode = failed.ErrorCode, CanRetry = true };
            }
            if (acquired is ProviderAcquisitionCancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            Assert.IsType<ProviderAcquisitionSucceeded>(acquired);
            Assert.Equal("connect-material", this.derivedMaterial);
            return new CredentialExecutionReady<ParentResult>
            {
                ResolvedIdentity = new()
                {
                    LogicalIdentifier = request.Credential.LogicalIdentifier,
                    SourceIdentifier = context.PolicyRequest!.SourceIdentifier,
                    AccountIdentifier = this.UsedAccount!,
                    RequestIdentifier = context.RequestIdentifier,
                    RequestGeneration = context.Revision,
                },
                Materializer = new Materializer(),
            };
        }

        private sealed class Operation(
            ParentExecutor parent,
            GitHubCredentialAccessExecutor provider,
            List<string> events,
            ICredentialExecutionContext context,
            ProviderAcquisitionKey key) : IProviderAcquisitionOperation
        {
            public ProviderAcquisitionKey CompatibilityKey => key;
            public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
            public long Revision => context.Revision;
            public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.DevTunnelConnect;
            public Guid AcquisitionGeneration => context.AcquisitionGeneration;

            public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
                ProviderAcquisitionCommitContext commitContext,
                CancellationToken cancellationToken)
            {
                parent.Context = context;
                parent.Permit = commitContext.InteractionPermit;
                if (parent.BodyOverride is { } body)
                {
                    return await body(context, commitContext, cancellationToken);
                }
                var used = parent.UseSplitBoundary
                    ? await this.UseSplitAsync(commitContext, cancellationToken)
                    : await provider.UsePrivateAsync(
                    parent.IdentityRequest,
                    context,
                    commitContext.InteractionPermit,
                    (identity, _) =>
                    {
                        Assert.Equal("identity-material", identity);
                        events.Add("mint");
                        return ValueTask.FromResult("connect-material");
                    },
                    cancellationToken);
                if (used is GitHubPrivateCredentialUseDenied<string>)
                {
                    return new ProviderAcquisitionDenied();
                }
                if (used is GitHubPrivateCredentialUseFailed<string> failed)
                {
                    return new ProviderAcquisitionFailed { ErrorCode = failed.ErrorCode };
                }
                var succeeded = Assert.IsType<GitHubPrivateCredentialUseSucceeded<string>>(used);
                parent.UsedAccount = succeeded.AccountIdentifier;
                parent.UsedGeneration = succeeded.Generation;
                Assert.NotEqual(Guid.Empty, succeeded.Generation.Value);
                return await commitContext.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                {
                    parent.derivedMaterial = succeeded.Result;
                    events.Add("connect committed");
                    return ValueTask.CompletedTask;
                });
            }

            private async ValueTask<GitHubPrivateCredentialUseOutcome<string>> UseSplitAsync(
                ProviderAcquisitionCommitContext parentCommit,
                CancellationToken cancellationToken)
            {
                var inspection = await provider.InspectPrivateAsync(parent.IdentityRequest, cancellationToken);
                CredentialMaterialGenerationIdentifier generation;
                if (inspection.Usability == CredentialSourceUsability.Usable)
                {
                    generation = inspection.CachedGeneration!.Generation;
                }
                else
                {
                    var acquisition = provider.CreatePrivateAcquisition(parent.IdentityRequest, inspection.Source, context);
                    Assert.Equal(inspection.AcquisitionKey, acquisition.CompatibilityKey);
                    var outcome = await context.ExecutePrivateProviderDependencyAsync(acquisition, parentCommit.InteractionPermit, cancellationToken);
                    if (outcome is ProviderAcquisitionFailed failed)
                    {
                        return new GitHubPrivateCredentialUseFailed<string> { ErrorCode = failed.ErrorCode, CanRetry = true };
                    }
                    generation = Assert.IsType<ProviderAcquisitionSucceeded>(outcome).CommittedGeneration;
                }
                if (parent.BeforeGenerationUse is { } beforeUse)
                {
                    await beforeUse(inspection, generation, cancellationToken);
                }
                return await provider.UseGenerationAsync(
                    inspection.Source, generation, context, parentCommit.InteractionPermit, (identity, account, _) =>
                    {
                        Assert.Equal("identity-material", identity);
                        Assert.NotNull(account);
                        events.Add("mint");
                        return ValueTask.FromResult("connect-material");
                    }, cancellationToken);
            }
        }

        private sealed class Materializer : IAccessResultMaterializer<ParentResult>
        {
            public ValueTask<ParentResult> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ParentResult());
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PolicyBarrier
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReleaseAuthorizationValidation? Validation { get; set; }
    }

    private sealed class PausingAcquisitionPolicy(ReleaseAuthorizationPolicy policy) :
        IReleaseAuthorizationPolicy, IProviderAcquisitionAuthorizationPolicy, IAsyncDisposable
    {
        private int remainingValidations;
        private PolicyBarrier? barrier;

        public PolicyBarrier Arm(int validations)
        {
            this.remainingValidations = validations;
            return this.barrier = new PolicyBarrier();
        }

        public async ValueTask<ReleaseAuthorizationValidation> ValidateAcquisitionAuthorizationAsync(
            ReleaseAuthorization authorization,
            ReleasePolicyRequest request,
            CancellationToken cancellationToken)
        {
            var validation = await ((IProviderAcquisitionAuthorizationPolicy)policy)
                .ValidateAcquisitionAuthorizationAsync(authorization, request, cancellationToken);
            if (this.barrier is { } pending && --this.remainingValidations == 0)
            {
                this.barrier = null;
                pending.Validation = validation;
                pending.Reached.SetResult();
                await pending.Continue.Task.WaitAsync(cancellationToken);
            }
            return validation;
        }

        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.EvaluateAsync(request, cancellationToken);
        public ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken = default) =>
            policy.CommitAsync(decision, cancellationToken);
        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.ValidateAsync(identifier, request, cancellationToken);
        public ValueTask FinalizeCredentialIdentityAsync(ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken = default) =>
            policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) =>
            policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => policy.DisposeAsync();
    }

    private sealed class BoundarySyncResult(Action cleanup) : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose()
        {
            this.Disposals++;
            cleanup();
        }
    }

    private sealed class BoundaryAsyncResult(Action cleanup) : IAsyncDisposable, IDisposable
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        public async ValueTask DisposeAsync()
        {
            this.AsyncDisposals++;
            await Task.Yield();
            cleanup();
        }
        public void Dispose() => this.SyncDisposals++;
    }

    private sealed class DerivedResult : IAsyncDisposable, IDisposable
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        public Func<ValueTask>? OnAsyncDispose { get; init; }

        public async ValueTask DisposeAsync()
        {
            this.AsyncDisposals++;
            if (this.OnAsyncDispose is { } dispose)
            {
                await dispose();
            }
        }
        public void Dispose() => this.SyncDisposals++;
    }

    private sealed class SynchronousDerivedResult : IDisposable
    {
        public int Disposals { get; private set; }
        public Action? OnDispose { get; init; }

        public void Dispose()
        {
            this.Disposals++;
            this.OnDispose?.Invoke();
        }
    }

    private sealed class Source(List<string> events) : IGitHubCredentialAccessSource
    {
        public int Acquisitions { get; private set; }
        public CredentialAccountIdentifier Account { get; set; } = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "identity-account");
        public string Material { get; set; } = "identity-material";
        public DateTimeOffset? ExpiresOn { get; init; }
        public Func<CancellationToken, ValueTask>? BeforeReturn { get; set; }
        public bool EnvironmentExists() => throw new InvalidOperationException("Forced CLI must not inspect another source.");

        public async ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            Assert.Equal(CredentialSourceKind.GitHubCli, source);
            this.Acquisitions++;
            events.Add("identity acquired");
            var result = new GitHubCredentialMaterial
            {
                Material = this.Material.ToCharArray(),
                Account = this.Account,
                ExpiresOn = this.ExpiresOn,
            };
            if (this.BeforeReturn is { } beforeReturn)
            {
                await beforeReturn(cancellationToken);
            }
            return result;
        }
    }
}
