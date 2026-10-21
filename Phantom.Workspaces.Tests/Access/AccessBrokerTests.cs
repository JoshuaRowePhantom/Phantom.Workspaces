using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class AccessBrokerTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-broker-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public async Task AccessBroker_Disposal_PreservesEveryCleanupFailureAndRejectsNewRequests()
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        var policy = new Mock<IReleaseAuthorizationPolicy>();
        var ownedPolicy = policy.As<IAsyncDisposable>();
        ownedPolicy.Setup(value => value.DisposeAsync())
            .Returns(() => ValueTask.FromException(new IOException("policy")));
        var coordinator = new FaultingCoordinator();
        var executor = new Executor
        {
            Execute = (_, _, _, _, _, _) => throw new InvalidOperationException("Must not execute."),
            Dispose = () => ValueTask.FromException(new InvalidOperationException("executor")),
        };
        executors.Add<RequestRecord, Result>(executor);
        var broker = new AccessBroker(policy.Object, executors, coordinator, registry);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => broker.DisposeAsync().AsTask());
        Assert.Equal(3, failure.Flatten().InnerExceptions.Count);
        var repeated = await Assert.ThrowsAsync<AggregateException>(() => broker.DisposeAsync().AsTask());
        Assert.Same(failure, repeated);
        ownedPolicy.Verify(value => value.DisposeAsync(), Times.Once);
        Assert.Equal(1, coordinator.DisposalCount);
        Assert.IsType<AccessShuttingDown<Result>>(await broker.AcquireAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public async Task AccessBroker_ExplicitRenewal_KeepsCallerPendingUntilNewApprovedGeneration()
    {
        var generations = new List<Guid>();
        var executor = new Executor
        {
            Execute = (_, _, _, permit, context, _) =>
            {
                Assert.NotNull(permit);
                generations.Add(context.AcquisitionGeneration);
                return ValueTask.FromResult<CredentialExecutionOutcome<Result>>(generations.Count == 1
                    ? new CredentialExecutionRenewalRequired<Result>()
                    : new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) });
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var initial = Assert.Single(registry.GetSnapshot());
        var renewed = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, UserAuthorizationChangedEventArgs arguments)
        {
            var snapshot = registry.GetSnapshot().Single();
            if (snapshot.State == UserAuthorizationState.RenewalNeeded)
            {
                renewed.TrySetResult(snapshot);
            }
        }
        registry.Changed += Changed;
        UserAuthorizationSnapshot renewal;
        try
        {
            await ApplyAsync(registry, initial);
            renewal = await renewed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            registry.Changed -= Changed;
        }

        Assert.False(task.IsCompleted);
        Assert.Single(generations);
        Assert.Equal(initial.Identifier, renewal.Identifier);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, renewal.PrimaryAction);
        await Assert.ThrowsAsync<UserAuthorizationActionRejectedException>(() => ApplyAsync(registry, initial));
        Assert.Single(generations);
        Assert.False(task.IsCompleted);

        await ApplyAsync(registry, renewal);
        await using var result = Assert.IsType<AccessSucceeded<Result>>(
            await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;

        Assert.Equal(2, generations.Count);
        Assert.NotEqual(generations[0], generations[1]);
        Assert.Equal(UserAuthorizationState.Completed, Assert.Single(registry.GetSnapshot()).State);
    }

    [Fact]
    public async Task AccessBroker_ExplicitRenewal_CancellationDoesNotStartAnotherProviderGeneration()
    {
        var executions = 0;
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor
        {
            Execute = (_, _, _, _, _, _) =>
            {
                executions++;
                attempted.TrySetResult();
                return ValueTask.FromResult<CredentialExecutionOutcome<Result>>(new CredentialExecutionRenewalRequired<Result>());
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = broker.AcquireAsync(Request(), cancellation.Token);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        Assert.IsType<AccessCancelled<Result>>(
            await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task AccessBroker_AcquireAsync_ReleaseCommitPrecedesProviderExecution()
    {
        var events = new List<string>();
        var executor = new Executor
        {
            Execute = async (request, inspection, authorization, permit, context, cancellationToken) =>
            {
                Assert.Equal(ReleaseAuthorizationValidation.Valid,
                    await context.Policy.ValidateAsync(authorization.Identifier, context.PolicyRequest!, cancellationToken));
                events.Add("release committed");
                Assert.NotNull(permit);
                var operation = new Operation(context, inspection.AcquisitionKeys[inspection.SelectedSource!], async commit =>
                {
                    events.Add("provider");
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                    {
                        events.Add("cache committed");
                        return ValueTask.CompletedTask;
                    });
                });
                Assert.IsType<ProviderAcquisitionSucceeded>(
                    await context.ExecuteProviderAcquisitionAsync(operation, permit, cancellationToken));
                return new CredentialExecutionReady<Result>
                {
                    Materializer = new Materializer(() =>
                    {
                        events.Add("materialize");
                        return new Result();
                    }),
                };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        Assert.Empty(events);
        await ApplyAsync(registry, row);
        var result = Assert.IsType<AccessSucceeded<Result>>(await task);
        Assert.Equal(new[] { "release committed", "provider", "cache committed", "materialize" }, events);
        Assert.Equal(UserAuthorizationState.Completed, Assert.Single(registry.GetSnapshot()).State);
        await result.Result.DisposeAsync();
    }

    [Fact]
    public async Task AccessBroker_AcquireAsync_DependencyCycle_ReturnsSafeFailure()
    {
        var executor = new Executor
        {
            Execute = async (request, _, _, _, context, cancellationToken) =>
            {
                var dependency = await context.AcquireDependencyAsync(request, cancellationToken);
                var failed = Assert.IsType<AccessFailed<Result>>(dependency);
                Assert.Equal(AccessErrorCode.DependencyCycle, failed.ErrorCode);
                return new CredentialExecutionFailed<Result> { ErrorCode = failed.ErrorCode, CanRetry = false };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        var failure = Assert.IsType<AccessFailed<Result>>(await task);
        Assert.Equal(AccessErrorCode.DependencyCycle, failure.ErrorCode);
        Assert.DoesNotContain("private", failure.SafeMessage);
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public async Task AccessBroker_DisposeAsync_RejectsNewWorkAndIgnoresLateCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var materializer = new Materializer(() => throw new InvalidOperationException("Late material must not be published."));
        var executor = new Executor
        {
            Execute = async (_, _, _, _, _, _) =>
            {
                started.SetResult();
                await finish.Task;
                return new CredentialExecutionReady<Result> { Materializer = materializer };
            },
        };
        var (broker, registry) = this.Create(executor);
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await broker.DisposeAsync();
        Assert.IsType<AccessShuttingDown<Result>>(await task);
        Assert.IsType<AccessShuttingDown<Result>>(await broker.AcquireAsync(Request(), TestContext.Current.CancellationToken));
        finish.SetResult();
        await materializer.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public async Task AccessBroker_CompatibleRows_KeepIndependentDecisionsAndMaterializeIndependentResults()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        var usable = false;
        var executor = new Executor
        {
            IsUsable = () => usable,
            Execute = async (_, inspection, _, permit, context, cancellationToken) =>
            {
                if (!usable)
                {
                    Assert.NotNull(permit);
                    var operation = new Operation(context, inspection.AcquisitionKeys[inspection.SelectedSource!], async commit =>
                    {
                        Interlocked.Increment(ref acquisitions);
                        started.TrySetResult();
                        await finish.Task.WaitAsync(cancellationToken);
                        return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                        {
                            usable = true;
                            return ValueTask.CompletedTask;
                        });
                    });
                    Assert.IsType<ProviderAcquisitionSucceeded>(
                        await context.ExecuteProviderAcquisitionAsync(operation, permit, cancellationToken));
                }

                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var firstTask = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var secondTask = broker.AcquireAsync(Request("second-use"), TestContext.Current.CancellationToken);
        var rows = registry.GetSnapshot();
        Assert.Equal(2, rows.Count);
        await ApplyAsync(registry, rows[0]);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(UserAuthorizationState.Pending, registry.GetSnapshot()[1].State);
        finish.SetResult();
        var first = Assert.IsType<AccessSucceeded<Result>>(await firstTask);
        var secondRow = registry.GetSnapshot()[1];
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, secondRow.PrimaryAction);
        Assert.False(secondTask.IsCompleted);
        await ApplyAsync(registry, secondRow);
        var second = Assert.IsType<AccessSucceeded<Result>>(await secondTask);
        Assert.NotSame(first.Result, second.Result);
        Assert.Equal(1, acquisitions);
        await first.Result.DisposeAsync();
        await second.Result.DisposeAsync();
    }

    [Fact]
    public async Task AccessBroker_DenialAndCancellation_DoNotExecuteOrLeaveActiveRows()
    {
        var executor = new Executor { Execute = (_, _, _, _, _, _) => throw new InvalidOperationException("Unauthorized execution.") };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var denied = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<Result>>(await denied);
        using var cancellation = new CancellationTokenSource();
        var cancelled = broker.AcquireAsync(Request("cancelled"), cancellation.Token);
        cancellation.Cancel();
        Assert.IsType<AccessCancelled<Result>>(await cancelled);
        Assert.DoesNotContain(registry.GetSnapshot(), snapshot => snapshot.CanAct);
    }

    [Fact]
    public async Task AccessBroker_CompletionCallerCancellationClosesRowBeforeReturning()
    {
        var executor = new CompletionExecutor();
        var executors = new CredentialAccessExecutorRegistry();
        executors.AddCompletion<CompletionRequest, CompletionResult>(executor);
        var registry = new UserAuthorizationRegistry();
        await using var broker = new AccessBroker(
            Mock.Of<IReleaseAuthorizationPolicy>(), executors, new ProviderAcquisitionCoordinator(), registry);
        var credentialRequest = Request();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = broker.AcquireAsync(new CompletionRequest
        {
            Requestor = credentialRequest.Requestor,
            Use = credentialRequest.Use with { MemoryScopes = [] },
            PresentationScope = AccessPresentationScope.Bootstrap,
        }, cancellation.Token);
        Assert.True(Assert.Single(registry.GetSnapshot()).CanAct);

        await cancellation.CancelAsync();

        Assert.IsType<AccessCancelled<CompletionResult>>(await pending);
        var cancelled = Assert.Single(registry.GetSnapshot());
        Assert.Equal(UserAuthorizationState.Cancelled, cancelled.State);
        Assert.False(cancelled.CanAct);
        Assert.Equal(0, executor.Executions);
    }

    [Fact]
    public async Task AccessBroker_LeaseRevalidation_CreatesOneActionableRowWithoutReplacingMaterial()
    {
        var executor = new Executor
        {
            IsUsable = () => true,
            Execute = (_, _, authorization, _, context, _) => ValueTask.FromResult<CredentialExecutionOutcome<Result>>(
                new CredentialExecutionReady<Result>
                {
                    Materializer = new Materializer(() => new Result
                    {
                        Lease = new SensitiveValueLease("fixed".AsMemory(), new CredentialLeaseBinding
                        {
                            AuthorizationIdentifier = authorization.Identifier,
                            Request = context.PolicyRequest!,
                            MaterialGeneration = new() { Value = Guid.NewGuid() },
                        }, context.CreateLeaseContext(new GenerationValidator())),
                    }),
                }),
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.ExactUse).Identifier,
            Source = row.SelectedSource,
        }, TestContext.Current.CancellationToken);
        await using var result = Assert.IsType<AccessSucceeded<Result>>(await task).Result;
        await broker.Policy.ForgetAsync(
            Assert.Single(await broker.Policy.ListAsync(TestContext.Current.CancellationToken)).Identifier,
            TestContext.Current.CancellationToken);
        var required = Assert.IsType<SensitiveValueUseAuthorizationRequired<bool>>(await result.Lease!.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException("Forgotten release memory must block material."),
            TestContext.Current.CancellationToken));
        var repeated = Assert.IsType<SensitiveValueUseAuthorizationRequired<bool>>(await result.Lease.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException(),
            TestContext.Current.CancellationToken));
        Assert.Equal(required.RequestIdentifier, repeated.RequestIdentifier);
        var renewal = Assert.Single(registry.GetSnapshot(), snapshot => snapshot.Identifier == required.RequestIdentifier);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, renewal.PrimaryAction);
        Assert.Equal(UserAuthorizationState.Pending, renewal.State);
    }

    [Fact]
    public async Task AccessBroker_CompletionOnlyAuthorization_RetriesWithoutReleasePolicyOrMaterial()
    {
        var policy = new Mock<IReleaseAuthorizationPolicy>(MockBehavior.Strict);
        var executor = new CompletionExecutor();
        var executors = new CredentialAccessExecutorRegistry();
        executors.AddCompletion<CompletionRequest, CompletionResult>(executor);
        var registry = new UserAuthorizationRegistry();
        await using var broker = new AccessBroker(policy.Object, executors, new ProviderAcquisitionCoordinator(), registry);
        var credentialRequest = Request();
        var request = new CompletionRequest
        {
            Requestor = credentialRequest.Requestor,
            Use = credentialRequest.Use with { MemoryScopes = Array.Empty<ReleaseMemoryScopeKind>() },
            PresentationScope = AccessPresentationScope.Bootstrap,
        };
        var first = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        Assert.IsType<AccessFailed<CompletionResult>>(await first);
        var failed = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new RetryUserAuthorizationAction
        {
            RequestIdentifier = failed.Identifier,
            ExpectedRevision = failed.Revision,
        }, TestContext.Current.CancellationToken);
        var completed = await registry.WaitForCompletionAsync(failed.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(UserAuthorizationState.Completed, completed.State);
        await executor.UnclaimedDisposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, executor.Executions);
        var silent = Assert.IsType<AccessSucceeded<CompletionResult>>(
            await broker.AcquireAsync(request, TestContext.Current.CancellationToken));
        await silent.Result.DisposeAsync();
        Assert.Single(registry.GetSnapshot());
        policy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AccessBroker_MaterializerCleanupFailure_DisposesUnpublishedResultAndPreservesFailure()
    {
        var result = new Result();
        var executor = new Executor
        {
            IsUsable = () => true,
            Execute = (_, _, _, _, _, _) => ValueTask.FromResult<CredentialExecutionOutcome<Result>>(
                new CredentialExecutionReady<Result> { Materializer = new FaultingMaterializer(result) }),
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("Materializer cleanup failed.", failure.Message);
        await result.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AccessExecutionContracts_CoreAndApplicationShareInterfacesWithoutReverseDependency()
    {
        var contracts = typeof(IAccessBroker).Assembly;
        Assert.Equal(contracts, typeof(ICredentialAccessExecutor<,>).Assembly);
        Assert.Equal(contracts, typeof(ICredentialExecutionContext).Assembly);
        Assert.Equal(contracts, typeof(ProviderInteractionPermit).Assembly);
        var friends = contracts.GetCustomAttributes<InternalsVisibleToAttribute>().Select(attribute => attribute.AssemblyName);
        Assert.Contains("Phantom.Workspaces.Llm.Core", friends);
        Assert.Contains("Phantom.Workspaces", friends);
        Assert.DoesNotContain(typeof(Phantom.Workspaces.Llm.Auth.EntraPinnedTokenProvider).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Phantom.Workspaces");
        Assert.DoesNotContain(contracts.GetReferencedAssemblies(), reference => reference.Name == "Phantom.Workspaces");
        Assert.Equal(new[] { "AccountIdentifier", "ExpiresOn", "Generation" },
            typeof(CredentialGenerationInspection).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(typeof(CredentialGenerationInvalidationOutcome),
            typeof(ICredentialGenerationCache).GetMethod(nameof(ICredentialGenerationCache.InvalidateGenerationAsync))!
                .ReturnType.GenericTypeArguments.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessBroker_PrivateProviderDependency_RequiresActivePerformAndNeverCreatesExportRow(bool perform)
    {
        ICredentialExecutionContext? savedContext = null;
        ProviderInteractionPermit? savedPermit = null;
        ProviderAcquisitionKey? savedKey = null;
        var commits = 0;
        var parentCommitted = false;
        var materialGeneration = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            IsUsable = () => !perform,
            Execute = async (_, inspection, _, permit, context, cancellationToken) =>
            {
                savedContext = context;
                savedPermit = permit;
                savedKey = inspection.AcquisitionKeys[inspection.SelectedSource!];
                if (permit is not null)
                {
                    Assert.False(await permit.AuthorizesPrivateUseAsync(context, cancellationToken));
                }
                var childKey = savedKey with
                {
                    Configuration = new() { Digest = new string('e', 64) },
                    Protocol = ProviderProtocolKind.Sdk,
                };
                var child = new Operation(context, childKey, async commit =>
                {
                    Assert.True(commit.InteractionPermit.Authorizes(
                        context.RequestIdentifier, context.Revision, CredentialExecutorKind.Entra,
                        context.AcquisitionGeneration, childKey));
                    Assert.True(commit.InteractionPermit.AuthorizesProvider(CredentialExecutorKind.Entra, childKey));
                    return await commit.CommitAsync(materialGeneration, _ =>
                    {
                        commits++;
                        return ValueTask.CompletedTask;
                    });
                }, CredentialExecutorKind.Entra);
                var parentPermit = permit ?? new ProviderInteractionPermit(
                    context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                    context.AcquisitionGeneration, savedKey, () => true);
                var acquired = await context.ExecutePrivateProviderDependencyAsync(child, parentPermit, cancellationToken);
                Assert.IsType<ProviderAcquisitionDenied>(acquired);
                if (perform)
                {
                    var parentOperation = new Operation(context, savedKey, async commit =>
                    {
                        savedPermit = commit.InteractionPermit;
                        Assert.True(await savedPermit.AuthorizesPrivateUseAsync(context, cancellationToken));
                        var privateOutcome = await context.ExecutePrivateProviderDependencyAsync(
                            child, savedPermit, cancellationToken);
                        Assert.Equal(materialGeneration,
                            Assert.IsType<ProviderAcquisitionSucceeded>(privateOutcome).CommittedGeneration);
                        return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                        {
                            parentCommitted = true;
                            return ValueTask.CompletedTask;
                        });
                    }, CredentialExecutorKind.DevTunnelConnect);
                    Assert.IsType<ProviderAcquisitionSucceeded>(
                        await context.ExecuteProviderAcquisitionAsync(parentOperation, permit!, cancellationToken));
                }

                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var original = Request();
        var request = original with
        {
            Credential = original.Credential with
            {
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "account-a"),
            },
            Sources = original.Sources.Select(source => source with { Provider = CredentialProviderKind.DevTunnel }).ToArray(),
        };
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await using var result = Assert.IsType<AccessSucceeded<Result>>(await task).Result;
        Assert.Equal(perform ? 1 : 0, commits);
        Assert.Equal(perform, parentCommitted);
        Assert.Equal(UserAuthorizationState.Completed, Assert.Single(registry.GetSnapshot()).State);
        Assert.NotNull(savedContext);
        var late = new Operation(savedContext, savedKey!, _ => throw new InvalidOperationException());
        if (savedPermit is not null)
        {
            Assert.IsType<ProviderAcquisitionDenied>(
                await savedContext.ExecutePrivateProviderDependencyAsync(late, savedPermit, TestContext.Current.CancellationToken));
            Assert.IsType<ProviderAcquisitionDenied>(await savedContext.ExecuteProviderAcquisitionAsync(
                late, savedPermit, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AccessBroker_ExportedDependency_PreservesParentPurposeLineageAndBootstrapPresentation()
    {
        var request = Request() with { PresentationScope = AccessPresentationScope.Bootstrap };
        var secondRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor
        {
            Execute = async (current, _, _, _, context, cancellationToken) =>
            {
                if (current.Credential.LogicalIdentifier == request.Credential.LogicalIdentifier)
                {
                    var dependency = Request("different-purpose") with
                    {
                        Credential = current.Credential with { LogicalIdentifier = new() { Digest = new string('e', 64) } },
                        Use = current.Use with { Lineage = new() },
                        PresentationScope = AccessPresentationScope.Runtime,
                    };
                    var acquired = await context.AcquireDependencyAsync(dependency, cancellationToken);
                    await Assert.IsType<AccessSucceeded<Result>>(acquired).Result.DisposeAsync();
                }
                else
                {
                    Assert.Equal(request.Requestor, current.Requestor);
                    Assert.Equal(request.Use, current.Use);
                    Assert.Equal(request.PresentationScope, current.PresentationScope);
                }

                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        registry.Changed += (_, _) =>
        {
            if (registry.GetSnapshot().Count == 2)
            {
                secondRow.TrySetResult();
            }
        };
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await secondRow.Task.WaitAsync(TestContext.Current.CancellationToken);
        var child = registry.GetSnapshot()[1];
        Assert.True(child.IsBootstrapBlocking);
        Assert.Equal(request.Use.SafeDisplayName, child.UseDisplayName);
        await ApplyAsync(registry, child);
        await using var result = Assert.IsType<AccessSucceeded<Result>>(await task).Result;
        Assert.All(registry.GetSnapshot(), snapshot => Assert.Equal(UserAuthorizationState.Completed, snapshot.State));
    }

    [Fact]
    public async Task AccessBroker_CacheDeleted_RetainsMemorySelectionButRequiresNewPerform()
    {
        var usable = true;
        var executions = 0;
        var executor = new Executor
        {
            IsUsable = () => usable,
            Execute = (_, _, _, _, _, _) =>
            {
                executions++;
                return ValueTask.FromResult<CredentialExecutionOutcome<Result>>(
                    new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) });
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var first = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        var initial = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = initial.Identifier,
            ExpectedRevision = initial.Revision,
            Memory = initial.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
            Source = initial.SelectedSource,
        }, TestContext.Current.CancellationToken);
        await Assert.IsType<AccessSucceeded<Result>>(await first).Result.DisposeAsync();
        usable = false;
        var renewed = broker.AcquireAsync(Request("another-use"), TestContext.Current.CancellationToken);
        var row = registry.GetSnapshot()[1];
        Assert.Equal(ReleaseMemoryScopeKind.AllUses, row.MemoryOptions.Single(option => option.Identifier == row.SelectedMemory).Scope);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        Assert.Equal(1, executions);
        Assert.False(renewed.IsCompleted);
        await registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<Result>>(await renewed);
        Assert.Equal(ReleaseMemoryScopeKind.AllUses,
            Assert.Single(await broker.Policy.ListAsync(TestContext.Current.CancellationToken)).Scope);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task AccessBroker_RememberedFallback_RejectsChangedAccountOrLogicalPins(
        bool pinRequestAccount,
        bool changeAccount)
    {
        var account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.StoredSecret, "account-a");
        var request = Request();
        request = request with
        {
            Credential = request.Credential with { AccountIdentifier = pinRequestAccount ? account : null },
        };
        var original = request.Credential with { AccountIdentifier = account };
        var changed = changeAccount
            ? original with { AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.StoredSecret, "account-b") }
            : original with { LogicalIdentifier = new() { Digest = new string('e', 64) } };
        var inspections = 0;
        var executions = 0;
        var materializations = 0;
        var executor = new Executor
        {
            IsUsable = () => true,
            RefineInspection = inspection => inspection with { Credential = ++inspections == 1 ? original : changed },
            Execute = (_, _, _, _, _, _) => ValueTask.FromResult<CredentialExecutionOutcome<Result>>(
                ++executions == 1
                    ? new CredentialExecutionMissing<Result>()
                    : new CredentialExecutionReady<Result>
                    {
                        Materializer = new Materializer(() =>
                        {
                            materializations++;
                            return new Result();
                        }),
                    }),
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        await RememberAsync(broker, request, original);
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = registry.GetSnapshot().SingleOrDefault();
        if (row is not null)
        {
            await ApplyAsync(registry, row);
        }

        var failed = Assert.IsType<AccessFailed<Result>>(await task);
        Assert.Equal(AccessErrorCode.IdentityMismatch, failed.ErrorCode);
        Assert.Equal(1, executions);
        Assert.Equal(0, materializations);
        Assert.Empty(registry.GetSnapshot());
        var memory = Assert.Single(await broker.Policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original.LogicalIdentifier, memory.LogicalIdentifier);
        Assert.Equal(account, memory.AccountIdentifier);
    }

    [Fact]
    public async Task AccessBroker_RememberedFallback_MissingSelectedSourceRetainsActionableRowWithoutSwitching()
    {
        var request = Request();
        var selected = request.Sources[0];
        var alternative = selected with
        {
            Identifier = new() { Digest = new string('e', 64) },
            Usability = CredentialSourceUsability.Usable,
        };
        request = request with { Sources = new[] { selected, alternative } };
        var inspections = 0;
        var executions = 0;
        var executor = new Executor
        {
            IsUsable = () => true,
            RefineInspection = inspection => ++inspections == 1
                ? inspection
                : inspection with { Sources = new[] { alternative } },
            Execute = (_, inspection, _, _, _, _) =>
            {
                executions++;
                Assert.Equal(selected.Identifier, inspection.SelectedSource);
                return ValueTask.FromResult<CredentialExecutionOutcome<Result>>(new CredentialExecutionMissing<Result>());
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        await RememberAsync(broker, request, request.Credential);
        var task = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        Assert.False(task.IsCompleted);
        Assert.Equal(selected.Identifier, row.SelectedSource);
        Assert.Equal(CredentialSourceUsability.Missing,
            row.SourceOptions.Single(source => source.Identifier == selected.Identifier).Usability);
        Assert.True(row.CanAct);
        await ApplyAsync(registry, row);
        var required = Assert.IsType<AccessReacquisitionRequired<Result>>(await task);
        Assert.Equal(row.Identifier, required.RequestIdentifier);
        var renewal = Assert.Single(registry.GetSnapshot());
        Assert.Equal(UserAuthorizationState.RenewalNeeded, renewal.State);
        Assert.Equal(selected.Identifier, renewal.SelectedSource);
        Assert.Equal(CredentialSourceUsability.Missing,
            renewal.SourceOptions.Single(source => source.Identifier == selected.Identifier).Usability);
        Assert.True(renewal.CanAct);
        Assert.Equal(1, executions);
        Assert.Single(await broker.Policy.ListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task RememberAsync(
        AccessBroker broker,
        RequestRecord request,
        CredentialReference credential)
    {
        await broker.Policy.CommitAsync(new ReleasePolicyDecision
        {
            Request = new()
            {
                RequestIdentifier = new() { Value = Guid.NewGuid() },
                RequestGeneration = 1,
                Credential = credential,
                SourceIdentifier = request.Sources[0].Identifier,
                RequestorIdentifier = request.Requestor.Identifier,
                Use = request.Use,
            },
            Scope = ReleaseMemoryScopeKind.AllUses,
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessBroker_PrivateCapability_RequiresCommittedActiveExecutionAndExactPolicyBinding(bool provisional)
    {
        var account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a");
        var request = ConnectRequest(provisional ? null : account);
        var executions = 0;
        ICredentialExecutionContext? completedContext = null;
        ProviderInteractionPermit? completedProof = null;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (_, inspection, authorization, admission, context, cancellationToken) =>
            {
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var childKey = key with { Configuration = new() { Digest = new string('e', 64) }, Protocol = ProviderProtocolKind.Sdk };
                Operation Child() => new(context, childKey, async commit =>
                {
                    executions++;
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                }, CredentialExecutorKind.Entra);
                Assert.NotNull(admission);
                Assert.False(await admission.AuthorizesPrivateUseAsync(context, cancellationToken));
                Assert.IsType<ProviderAcquisitionDenied>(
                    await context.ExecutePrivateProviderDependencyAsync(Child(), admission, cancellationToken));
                Assert.Equal(0, executions);
                var parent = new Operation(context, key, async commit =>
                {
                    var active = commit.InteractionPermit;
                    completedContext = context;
                    completedProof = active;
                    var binding = new ProviderExecutionAuthorizationBinding
                    {
                        Policy = context.Policy,
                        Authorization = authorization,
                        Request = context.PolicyRequest!,
                        AcquisitionKey = key,
                    };
                    ProviderInteractionPermit Bound(
                        CredentialExecutorKind kind,
                        ProviderAcquisitionKey candidate,
                        ProviderExecutionAuthorizationBinding? evidence = null,
                        bool current = true,
                        UserAuthorizationRequestIdentifier? identifier = null,
                        long? revision = null,
                        Guid? generation = null) => new ProviderInteractionPermit(
                            identifier ?? context.RequestIdentifier, revision ?? context.Revision, kind,
                            generation ?? context.AcquisitionGeneration, candidate, () => true, evidence ?? binding)
                            .BindExecution(() => current);
                    var unbound = new ProviderInteractionPermit(
                        context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                        context.AcquisitionGeneration, key, () => true).BindExecution(() => true);
                    foreach (var denied in new ProviderInteractionPermit[]
                    {
                        admission,
                        unbound,
                        Bound(CredentialExecutorKind.Entra, key),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key with { Protocol = ProviderProtocolKind.Sdk }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key with { Configuration = new() { Digest = new string('f', 64) } }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key with { Source = new() { Digest = new string('f', 64) } }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key with { Account = provisional ? account : null }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, current: false),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, identifier: new() { Value = Guid.NewGuid() }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, revision: context.Revision + 1),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, generation: Guid.NewGuid()),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, binding with
                        {
                            Policy = new Mock<IReleaseAuthorizationPolicy>().Object,
                        }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, binding with
                        {
                            Authorization = authorization with { Identifier = new() { Value = Guid.NewGuid() } },
                        }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, binding with
                        {
                            Authorization = authorization with { Scope = ReleaseMemoryScopeKind.AllUses },
                        }),
                        Bound(CredentialExecutorKind.DevTunnelConnect, key, binding with
                        {
                            Request = binding.Request with { SourceIdentifier = new() { Digest = new string('f', 64) } },
                        }),
                    })
                    {
                        Assert.False(await denied.AuthorizesPrivateUseAsync(context, cancellationToken));
                        Assert.IsType<ProviderAcquisitionDenied>(
                            await context.ExecutePrivateProviderDependencyAsync(Child(), denied, cancellationToken));
                    }

                    Assert.Equal(0, executions);
                    Assert.Equal(provisional ? ReleaseAuthorizationValidation.IdentityMismatch : ReleaseAuthorizationValidation.Valid,
                        await context.Policy.ValidateAsync(authorization.Identifier, context.PolicyRequest!, cancellationToken));
                    Assert.True(await active.AuthorizesPrivateUseAsync(context, cancellationToken));
                    if (!provisional)
                    {
                        var unpinned = new Operation(context, childKey with { Account = null }, _ =>
                        {
                            executions++;
                            throw new InvalidOperationException("A known parent account cannot become an unpinned child.");
                        }, CredentialExecutorKind.Entra);
                        Assert.IsType<ProviderAcquisitionDenied>(
                            await context.ExecutePrivateProviderDependencyAsync(unpinned, active, cancellationToken));
                        Assert.Equal(0, executions);
                    }
                    Assert.IsType<ProviderAcquisitionSucceeded>(
                        await context.ExecutePrivateProviderDependencyAsync(Child(), active, cancellationToken));
                    Assert.Equal(1, executions);
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                }, CredentialExecutorKind.DevTunnelConnect);
                Assert.IsType<ProviderAcquisitionSucceeded>(
                    await context.ExecuteProviderAcquisitionAsync(parent, admission, cancellationToken));
                return new CredentialExecutionReady<Result>
                {
                    ResolvedIdentity = new()
                    {
                        LogicalIdentifier = request.Credential.LogicalIdentifier,
                        SourceIdentifier = context.PolicyRequest!.SourceIdentifier,
                        AccountIdentifier = account,
                        RequestIdentifier = context.RequestIdentifier,
                        RequestGeneration = context.Revision,
                    },
                    Materializer = new Materializer(() => new Result()),
                };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await using var result = Assert.IsType<AccessSucceeded<Result>>(await pending).Result;
        Assert.NotNull(completedContext);
        Assert.NotNull(completedProof);
        Assert.False(await completedProof.AuthorizesPrivateUseAsync(completedContext, TestContext.Current.CancellationToken));
        Assert.Equal(1, executions);
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public async Task AccessBroker_PrivateCapability_RevokedPolicyDeniesBeforePrivateChildExecution()
    {
        var account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a");
        var request = ConnectRequest(account);
        var executions = 0;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (_, inspection, authorization, admission, context, cancellationToken) =>
            {
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var operation = new Operation(context, key, async commit =>
                {
                    var active = commit.InteractionPermit;
                    Assert.True(await active.AuthorizesPrivateUseAsync(context, cancellationToken));
                    Assert.NotNull(authorization.MemoryIdentifier);
                    await context.Policy.ForgetAsync(authorization.MemoryIdentifier, cancellationToken);
                    Assert.False(await active.AuthorizesPrivateUseAsync(context, cancellationToken));
                    var child = new Operation(context, key with { Protocol = ProviderProtocolKind.Sdk }, _ =>
                    {
                        executions++;
                        throw new InvalidOperationException("Revoked policy must deny before child execution.");
                    }, CredentialExecutorKind.Entra);
                    Assert.IsType<ProviderAcquisitionDenied>(
                        await context.ExecutePrivateProviderDependencyAsync(child, active, cancellationToken));
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                }, CredentialExecutorKind.DevTunnelConnect);
                Assert.NotNull(admission);
                var outcome = await context.ExecuteProviderAcquisitionAsync(operation, admission, cancellationToken);
                if (outcome is not ProviderAcquisitionSucceeded)
                {
                    return new CredentialExecutionFailed<Result>
                    {
                        ErrorCode = AccessErrorCode.ProviderFailure,
                        CanRetry = false,
                    };
                }
                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Source = row.SelectedSource,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(AccessErrorCode.ProviderFailure, Assert.IsType<AccessFailed<Result>>(await pending).ErrorCode);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task AccessBroker_PrivateCapability_ApprovedFollowerKeepsParentProofAndChildAlive()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ParticipantTrackingCoordinator();
        var childExecutions = 0;
        ICredentialExecutionContext? leaderContext = null;
        ProviderInteractionPermit? groupProof = null;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (_, inspection, _, admission, context, cancellationToken) =>
            {
                Assert.NotNull(admission);
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var operation = new GroupOperation(context, key, async (commit, groupCancellation) =>
                {
                    leaderContext = context;
                    groupProof = commit.InteractionPermit;
                    started.TrySetResult();
                    await finish.Task.WaitAsync(groupCancellation);
                    Assert.True(await groupProof.AuthorizesPrivateUseAsync(context, groupCancellation));
                    var child = new Operation(context, key with
                    {
                        Configuration = new() { Digest = new string('e', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                    }, async childCommit =>
                    {
                        childExecutions++;
                        return await childCommit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                    }, CredentialExecutorKind.Entra);
                    Assert.IsType<ProviderAcquisitionSucceeded>(
                        await context.ExecutePrivateProviderDependencyAsync(child, groupProof, CancellationToken.None));
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                });
                var outcome = await context.ExecuteProviderAcquisitionAsync(operation, admission, cancellationToken);
                if (outcome is not ProviderAcquisitionSucceeded)
                {
                    return new CredentialExecutionFailed<Result>
                    {
                        ErrorCode = AccessErrorCode.ProviderFailure,
                        CanRetry = false,
                    };
                }
                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor, coordinator);
        await using var owned = broker;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = ConnectRequest(CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a"));
        var first = broker.AcquireAsync(request, cancellation.Token);
        var second = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await ApplyAsync(registry, registry.GetSnapshot()[0]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await ApplyAsync(registry, registry.GetSnapshot()[1]);
        await coordinator.FollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.IsType<AccessCancelled<Result>>(
            await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.NotNull(groupProof);
        Assert.NotNull(leaderContext);
        Assert.True(await groupProof.AuthorizesPrivateUseAsync(leaderContext, TestContext.Current.CancellationToken));
        finish.TrySetResult();
        await using var result = Assert.IsType<AccessSucceeded<Result>>(
            await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, childExecutions);
        Assert.False(await groupProof.AuthorizesPrivateUseAsync(leaderContext, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    public async Task AccessBroker_PrivateChildNoneToken_OwnedByDistinctParentGroups(
        int parents, bool providerStarted, bool cancelAll)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ParticipantTrackingCoordinator();
        var executions = 0;
        var publications = 0;
        var childRuns = 0;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (_, inspection, _, admission, context, cancellationToken) =>
            {
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var parent = new GroupOperation(context, key, async (commit, _) =>
                {
                    var child = new GroupOperation(context, key with
                    {
                        Configuration = new() { Digest = new string('e', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                    }, async (childCommit, childCancellation) =>
                    {
                        Interlocked.Increment(ref childRuns);
                        using var registration = childCancellation.Register(() => childCancelled.TrySetResult());
                        try
                        {
                            if (providerStarted)
                            {
                                Interlocked.Increment(ref executions);
                            }
                            entered.TrySetResult();
                            // The started provider deliberately ignores cancellation until guarded commit.
                            if (providerStarted)
                            {
                                await proceed.Task;
                            }
                            else
                            {
                                await proceed.Task.WaitAsync(childCancellation);
                                Interlocked.Increment(ref executions);
                            }
                            return await childCommit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                            {
                                Interlocked.Increment(ref publications);
                                return ValueTask.CompletedTask;
                            });
                        }
                        catch (OperationCanceledException) when (childCancellation.IsCancellationRequested)
                        {
                            childCancelled.TrySetResult();
                            throw;
                        }
                        finally
                        {
                            childFinished.TrySetResult();
                        }
                    }, CredentialExecutorKind.Entra);
                    var outcome = await context.ExecutePrivateProviderDependencyAsync(
                        child, commit.InteractionPermit, CancellationToken.None);
                    return outcome is ProviderAcquisitionSucceeded
                        ? await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask)
                        : outcome;
                });
                var outcome = await context.ExecuteProviderAcquisitionAsync(parent, admission!, cancellationToken);
                return outcome is ProviderAcquisitionSucceeded
                    ? new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) }
                    : new CredentialExecutionFailed<Result> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = false };
            },
        };
        var (broker, registry) = this.Create(executor, coordinator);
        await using var owned = broker;
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = ConnectRequest(CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a"));
        var first = broker.AcquireAsync(request, firstCancellation.Token);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task<AccessOutcome<Result>>? second = null;
        if (parents == 2)
        {
            second = broker.AcquireAsync(request with
            {
                Credential = request.Credential with { LogicalIdentifier = new() { Digest = new string('b', 64) } },
            }, secondCancellation.Token);
            await ApplyAsync(registry, registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending));
            await coordinator.ChildFollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        firstCancellation.Cancel();
        Assert.IsType<AccessCancelled<Result>>(await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        if (cancelAll)
        {
            secondCancellation.Cancel();
            if (second is not null)
            {
                Assert.IsType<AccessCancelled<Result>>(await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            await childCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            proceed.TrySetResult();
            await childFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, publications);
            Assert.Equal(providerStarted ? 1 : 0, executions);
        }
        else
        {
            Assert.False(childCancelled.Task.IsCompleted);
            proceed.TrySetResult();
            await using var result = Assert.IsType<AccessSucceeded<Result>>(
                await second!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
            Assert.Equal(1, publications);
            Assert.Equal(1, executions);
        }
        Assert.Equal(1, childRuns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AccessBroker_PrivateCapability_ForgottenLeaderDoesNotRevokeAdmittedFollower(
        bool invalidateAll, bool childAlreadyStarted)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonadmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ParticipantTrackingCoordinator();
        var grants = new Dictionary<UserAuthorizationRequestIdentifier, ReleaseAuthorization>();
        var contexts = new Dictionary<UserAuthorizationRequestIdentifier, ICredentialExecutionContext>();
        var childExecutions = 0;
        var publications = 0;
        var materializations = 0;
        ICredentialExecutionContext? leaderContext = null;
        ProviderInteractionPermit? proof = null;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (request, inspection, authorization, admission, context, cancellationToken) =>
            {
                grants.Add(context.RequestIdentifier, authorization);
                contexts.Add(context.RequestIdentifier, context);
                if (request.Use.SafeDisplayName == "Not admitted")
                {
                    nonadmitted.TrySetResult();
                    await never.Task.WaitAsync(cancellationToken);
                }
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var parent = new GroupOperation(context, key, async (commit, _) =>
                {
                    leaderContext = context;
                    proof = commit.InteractionPermit;
                    started.TrySetResult();
                    await proceed.Task;
                    if (!await proof.AuthorizesPrivateUseAsync(context, CancellationToken.None))
                    {
                        return new ProviderAcquisitionDenied();
                    }
                    var child = new Operation(context, key with
                    {
                        Configuration = new() { Digest = new string('e', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                    }, async childCommit =>
                    {
                        childExecutions++;
                        childStarted.TrySetResult();
                        await childProceed.Task;
                        return await childCommit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                        {
                            publications++;
                            return ValueTask.CompletedTask;
                        });
                    }, CredentialExecutorKind.Entra);
                    var outcome = await context.ExecutePrivateProviderDependencyAsync(child, proof, CancellationToken.None);
                    return outcome is ProviderAcquisitionSucceeded
                        ? await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask)
                        : outcome;
                });
                var acquired = await context.ExecuteProviderAcquisitionAsync(parent, admission!, cancellationToken);
                return acquired is ProviderAcquisitionSucceeded
                    ? new CredentialExecutionReady<Result>
                    {
                        Materializer = new Materializer(() =>
                        {
                            Interlocked.Increment(ref materializations);
                            return new Result();
                        }),
                    }
                    : new CredentialExecutionFailed<Result> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = false };
            },
        };
        var (broker, registry) = this.Create(executor, coordinator);
        await using var owned = broker;
        var request = ConnectRequest(CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a"));
        var first = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var firstRow = Assert.Single(registry.GetSnapshot());
        await Approve(firstRow, ReleaseMemoryScopeKind.AllUses);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = broker.AcquireAsync(request with { Use = Request("follower").Use }, TestContext.Current.CancellationToken);
        var secondRow = registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, secondRow.PrimaryAction);
        Assert.False(second.IsCompleted);
        await Approve(secondRow, invalidateAll ? ReleaseMemoryScopeKind.AllUses : ReleaseMemoryScopeKind.AllowOnce);
        await coordinator.FollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(await proof!.AuthorizesPrivateUseAsync(
            contexts[secondRow.Identifier], TestContext.Current.CancellationToken));

        using var unjoinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<AccessOutcome<Result>>? unjoined = null;
        AccessBroker? otherBroker = null;
        Task<AccessOutcome<Result>>? wrongPolicy = null;
        if (invalidateAll)
        {
            unjoined = broker.AcquireAsync(request with
            {
                Use = Request("non-admitted").Use with { SafeDisplayName = "Not admitted" },
            }, unjoinedCancellation.Token);
            await Approve(registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending),
                ReleaseMemoryScopeKind.AllowOnce);
            await nonadmitted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(await proof.AuthorizesPrivateUseAsync(
                contexts.Values.Single(context => context.Use.SafeDisplayName == "Not admitted"),
                TestContext.Current.CancellationToken));
            UserAuthorizationRegistry otherRegistry;
            (otherBroker, otherRegistry) = this.Create(executor, coordinator);
            wrongPolicy = otherBroker.AcquireAsync(request with { Use = Request("other-policy").Use },
                TestContext.Current.CancellationToken);
            await ApplyAsync(otherRegistry, Assert.Single(otherRegistry.GetSnapshot()));
            await coordinator.AnotherFollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(await proof.AuthorizesPrivateUseAsync(
                contexts.Values.Single(context => ReferenceEquals(context.Policy, otherBroker.Policy)),
                TestContext.Current.CancellationToken));
        }
        if (childAlreadyStarted)
        {
            proceed.TrySetResult();
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        await broker.Policy.ForgetAsync(grants[firstRow.Identifier].MemoryIdentifier!, TestContext.Current.CancellationToken);
        if (invalidateAll)
        {
            await broker.Policy.ForgetAsync(grants[secondRow.Identifier].MemoryIdentifier!, TestContext.Current.CancellationToken);
        }
        Assert.Equal(!invalidateAll, await proof!.AuthorizesPrivateUseAsync(leaderContext, TestContext.Current.CancellationToken));
        proceed.TrySetResult();
        childProceed.TrySetResult();
        var firstOutcome = await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var secondOutcome = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.IsNotType<AccessSucceeded<Result>>(firstOutcome);
        if (invalidateAll)
        {
            Assert.IsNotType<AccessSucceeded<Result>>(secondOutcome);
            Assert.Equal(0, publications);
            Assert.Equal(0, materializations);
            Assert.Equal(childAlreadyStarted ? 1 : 0, childExecutions);
            unjoinedCancellation.Cancel();
            Assert.IsType<AccessCancelled<Result>>(await unjoined!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.IsNotType<AccessSucceeded<Result>>(
                await wrongPolicy!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await otherBroker!.DisposeAsync();
        }
        else
        {
            Assert.IsType<AccessAuthorizationRequired<Result>>(firstOutcome);
            await using var result = Assert.IsType<AccessSucceeded<Result>>(secondOutcome).Result;
            Assert.Equal(1, childExecutions);
            Assert.Equal(1, publications);
            Assert.Equal(1, materializations);
        }

        Task Approve(UserAuthorizationSnapshot row, ReleaseMemoryScopeKind scope) =>
            registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Source = row.SelectedSource,
                Memory = row.MemoryOptions.Single(option => option.Scope == scope).Identifier,
            }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AccessBroker_PrivateCapability_ExactUnboundIssuedKeyRetainsSelectedCachedAccount(int resultAccount)
    {
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a");
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-b");
        var request = ConnectRequest(null);
        request = request with
        {
            Sources = new[]
            {
                request.Sources[0],
                request.Sources[0] with { Identifier = new() { Digest = new string('c', 64) } },
            },
        };
        var selected = request.Sources[0].Identifier;
        var cachedGeneration = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
        var childExecutions = 0;
        var publications = 0;
        var materializations = 0;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            InspectOverride = candidate => new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = candidate.Credential with { AccountIdentifier = accountA },
                Sources = candidate.Sources,
                CachedGenerations = new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>
                {
                    [selected] = new()
                    {
                        Generation = cachedGeneration,
                        AccountIdentifier = accountA,
                        ExpiresOn = DateTimeOffset.UtcNow.AddHours(-1),
                    },
                },
                AcquisitionKeys = candidate.Sources.ToDictionary(source => source.Identifier, source => new ProviderAcquisitionKey
                {
                    Configuration = candidate.Credential.LogicalIdentifier,
                    Source = source.Identifier,
                    Account = null,
                    Protocol = ProviderProtocolKind.TunnelConnect,
                }),
            },
            Execute = async (_, inspection, authorization, admission, context, cancellationToken) =>
            {
                Assert.Equal(2, inspection.Sources.Count);
                Assert.Equal(cachedGeneration, inspection.CachedGenerations[selected].Generation);
                Assert.Equal(accountA, context.PolicyRequest!.Credential.AccountIdentifier);
                var issuedKey = inspection.AcquisitionKeys[selected];
                Assert.Null(issuedKey.Account);
                var operation = new GroupOperation(context, issuedKey, async (commit, _) =>
                {
                    var active = commit.InteractionPermit;
                    Assert.True(await active.AuthorizesPrivateUseAsync(context, cancellationToken));
                    var binding = new ProviderExecutionAuthorizationBinding
                    {
                        Policy = context.Policy,
                        Authorization = authorization,
                        Request = context.PolicyRequest,
                        AcquisitionKey = issuedKey,
                    };
                    foreach (var wrongKey in new[]
                    {
                        issuedKey with { Account = accountA },
                        issuedKey with { Account = accountB },
                        issuedKey with { Configuration = new() { Digest = new string('f', 64) } },
                        issuedKey with { Source = request.Sources[1].Identifier },
                        issuedKey with { Protocol = ProviderProtocolKind.Sdk },
                    })
                    {
                        var substituted = new ProviderInteractionPermit(
                            context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                            context.AcquisitionGeneration, wrongKey, () => true, binding).BindExecution(() => true);
                        Assert.False(await substituted.AuthorizesPrivateUseAsync(context, cancellationToken));
                        var substitutedBinding = new ProviderInteractionPermit(
                            context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                            context.AcquisitionGeneration, issuedKey, () => true,
                            binding with { AcquisitionKey = wrongKey }).BindExecution(() => true);
                        Assert.False(await substitutedBinding.AuthorizesPrivateUseAsync(context, cancellationToken));
                    }
                    var childKey = issuedKey with
                    {
                        Configuration = new() { Digest = new string('e', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                        Account = accountA,
                    };
                    foreach (var wrongAccount in new CredentialAccountIdentifier?[] { null, accountB })
                    {
                        var wrongChild = new Operation(context, childKey with { Account = wrongAccount }, _ =>
                            throw new InvalidOperationException("Policy account A cannot admit an unpinned or B child."),
                            CredentialExecutorKind.Entra);
                        Assert.IsType<ProviderAcquisitionDenied>(
                            await context.ExecutePrivateProviderDependencyAsync(wrongChild, active, CancellationToken.None));
                    }
                    var child = new Operation(context, childKey, async childCommit =>
                    {
                        childExecutions++;
                        return await childCommit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                    }, CredentialExecutorKind.Entra);
                    Assert.IsType<ProviderAcquisitionSucceeded>(
                        await context.ExecutePrivateProviderDependencyAsync(child, active, CancellationToken.None));
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                    {
                        publications++;
                        return ValueTask.CompletedTask;
                    });
                });
                Assert.IsType<ProviderAcquisitionSucceeded>(
                    await context.ExecuteProviderAcquisitionAsync(operation, admission!, cancellationToken));
                return new CredentialExecutionReady<Result>
                {
                    ResolvedIdentity = new()
                    {
                        LogicalIdentifier = request.Credential.LogicalIdentifier,
                        SourceIdentifier = selected,
                        AccountIdentifier = resultAccount == 0 ? accountA : resultAccount == 1 ? accountB : null!,
                        RequestIdentifier = context.RequestIdentifier,
                        RequestGeneration = context.Revision,
                    },
                    Materializer = new Materializer(() =>
                    {
                        materializations++;
                        return new Result();
                    }),
                };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Source = selected,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllowOnce).Identifier,
        }, TestContext.Current.CancellationToken);
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (resultAccount == 0)
        {
            await using var result = Assert.IsType<AccessSucceeded<Result>>(outcome).Result;
            Assert.Equal(1, materializations);
        }
        else
        {
            Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<Result>>(outcome).ErrorCode);
            Assert.Equal(0, materializations);
        }
        Assert.Equal(1, childExecutions);
        Assert.Equal(1, publications);
    }

    [Fact]
    public async Task AccessBroker_PrivateCapability_AccountlessIssuedKeyCannotSubstituteAnotherMembersPolicyAccount()
    {
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a");
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-b");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ParticipantTrackingCoordinator();
        ReleaseAuthorization? leaderAuthorization = null;
        ReleaseAuthorization? followerAuthorization = null;
        ReleasePolicyRequest? followerRequest = null;
        var materializations = 0;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            InspectOverride = request => new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential with
                {
                    AccountIdentifier = request.Use.SafeDisplayName == "Account B" ? accountB : accountA,
                },
                Sources = request.Sources,
                CachedGenerations = request.Sources.ToDictionary(source => source.Identifier, _ => new CredentialGenerationInspection
                {
                    Generation = new() { Value = Guid.NewGuid() },
                    AccountIdentifier = request.Use.SafeDisplayName == "Account B" ? accountB : accountA,
                    ExpiresOn = DateTimeOffset.UtcNow.AddHours(-1),
                }),
                AcquisitionKeys = request.Sources.ToDictionary(source => source.Identifier, source => new ProviderAcquisitionKey
                {
                    Configuration = request.Credential.LogicalIdentifier,
                    Source = source.Identifier,
                    Account = null,
                    Protocol = ProviderProtocolKind.TunnelConnect,
                }),
            },
            Execute = async (_, inspection, authorization, admission, context, cancellationToken) =>
            {
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                Assert.Null(key.Account);
                if (context.PolicyRequest!.Credential.AccountIdentifier == accountB)
                {
                    followerAuthorization = authorization;
                    followerRequest = context.PolicyRequest;
                }
                var operation = new GroupOperation(context, key, async (commit, _) =>
                {
                    leaderAuthorization = authorization;
                    Assert.Equal(accountA, context.PolicyRequest!.Credential.AccountIdentifier);
                    Assert.True(await commit.InteractionPermit.AuthorizesPrivateUseAsync(context, cancellationToken));
                    started.TrySetResult();
                    await proceed.Task;
                    Assert.False(await commit.InteractionPermit.AuthorizesPrivateUseAsync(context, cancellationToken));
                    return new ProviderAcquisitionDenied();
                });
                var acquired = await context.ExecuteProviderAcquisitionAsync(operation, admission!, cancellationToken);
                return acquired is ProviderAcquisitionSucceeded
                    ? new CredentialExecutionReady<Result>
                    {
                        Materializer = new Materializer(() =>
                        {
                            Interlocked.Increment(ref materializations);
                            return new Result();
                        }),
                    }
                    : new CredentialExecutionFailed<Result> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = false };
            },
        };
        var (broker, registry) = this.Create(executor, coordinator);
        await using var owned = broker;
        var request = ConnectRequest(null);
        var first = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var firstRow = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = firstRow.Identifier,
            ExpectedRevision = firstRow.Revision,
            Source = firstRow.SelectedSource,
            Memory = firstRow.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
        }, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = broker.AcquireAsync(request with { Use = request.Use with { SafeDisplayName = "Account B" } },
            TestContext.Current.CancellationToken);
        await ApplyAsync(registry, registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending));
        await coordinator.FollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await broker.Policy.ForgetAsync(leaderAuthorization!.MemoryIdentifier!, TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseAuthorizationValidation.Valid, await broker.Policy.ValidateAsync(
            followerAuthorization!.Identifier, followerRequest!, TestContext.Current.CancellationToken));
        proceed.TrySetResult();
        Assert.IsType<AccessFailed<Result>>(
            await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.IsType<AccessFailed<Result>>(
            await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(0, materializations);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public async Task AccessBroker_PrivateAndOrdinaryIdentityParticipants_ValidateTheirOwnAuthority(
        bool privateLeads, bool cancelLeader, bool revokeSurvivor)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var privateDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ParticipantTrackingCoordinator();
        var parentRequest = ConnectRequest(CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a"));
        var identityRequest = new IdentityRequestRecord
        {
            Credential = parentRequest.Credential with
            {
                LogicalIdentifier = new() { Digest = new string('e', 64) },
                Provider = CredentialProviderKind.Entra,
                Kind = CredentialKind.AccessToken,
            },
            Sources = parentRequest.Sources.Select(source => source with { Provider = CredentialProviderKind.Entra }).ToArray(),
            Requestor = parentRequest.Requestor,
            Use = Request("ordinary identity").Use,
            PresentationScope = AccessPresentationScope.Runtime,
        };
        var childKey = new ProviderAcquisitionKey
        {
            Configuration = identityRequest.Credential.LogicalIdentifier,
            Source = identityRequest.Sources[0].Identifier,
            Account = identityRequest.Credential.AccountIdentifier,
            Protocol = ProviderProtocolKind.Sdk,
        };
        var providerExecutions = 0;
        var publications = 0;
        var parentMaterializations = 0;
        var ordinaryMaterializations = 0;
        ReleaseAuthorization? parentAuthorization = null;
        ReleaseAuthorization? ordinaryAuthorization = null;
        async ValueTask<ProviderAcquisitionOutcome> Provider(
            ProviderAcquisitionCommitContext commit, CancellationToken _)
        {
            Interlocked.Increment(ref providerExecutions);
            started.TrySetResult();
            await proceed.Task;
            return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
            {
                Interlocked.Increment(ref publications);
                return ValueTask.CompletedTask;
            });
        }
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (_, inspection, authorization, admission, context, cancellationToken) =>
            {
                parentAuthorization = authorization;
                var parent = new GroupOperation(context, inspection.AcquisitionKeys[inspection.SelectedSource!],
                    async (commit, _) =>
                    {
                        ProviderAcquisitionOutcome child;
                        try
                        {
                            child = await context.ExecutePrivateProviderDependencyAsync(
                                new GroupOperation(context, childKey, Provider, CredentialExecutorKind.Entra),
                                commit.InteractionPermit, CancellationToken.None);
                        }
                        finally
                        {
                            privateDetached.TrySetResult();
                        }
                        return child is ProviderAcquisitionSucceeded
                            ? await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask)
                            : child;
                    });
                var acquired = await context.ExecuteProviderAcquisitionAsync(parent, admission!, cancellationToken);
                return acquired is ProviderAcquisitionSucceeded
                    ? new CredentialExecutionReady<Result>
                    {
                        Materializer = new Materializer(() =>
                        {
                            Interlocked.Increment(ref parentMaterializations);
                            return new Result();
                        }),
                    }
                    : new CredentialExecutionFailed<Result> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = false };
            },
        };
        var identityExecutor = new IdentityExecutor(childKey, async (context, authorization, admission, cancellationToken) =>
        {
            ordinaryAuthorization = authorization;
            ProviderAcquisitionOutcome acquired;
            try
            {
                acquired = await context.ExecuteProviderAcquisitionAsync(
                    new GroupOperation(context, childKey, Provider, CredentialExecutorKind.Entra), admission, cancellationToken);
            }
            finally
            {
                ordinaryDetached.TrySetResult();
            }
            return acquired is ProviderAcquisitionSucceeded
                ? new CredentialExecutionReady<Result>
                {
                    Materializer = new Materializer(() =>
                    {
                        Interlocked.Increment(ref ordinaryMaterializations);
                        return new Result();
                    }),
                }
                : new CredentialExecutionFailed<Result> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = false };
        });
        var (broker, registry) = this.Create(executor, coordinator, identityExecutor);
        await using var owned = broker;
        using var parentCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var ordinaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<AccessOutcome<Result>>? parentPending = null;
        Task<AccessOutcome<Result>>? ordinaryPending = null;
        if (privateLeads)
        {
            parentPending = broker.AcquireAsync(parentRequest, parentCancellation.Token);
        }
        else
        {
            ordinaryPending = broker.AcquireAsync(identityRequest, ordinaryCancellation.Token);
        }
        await Approve();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (privateLeads)
        {
            ordinaryPending = broker.AcquireAsync(identityRequest, ordinaryCancellation.Token);
        }
        else
        {
            parentPending = broker.AcquireAsync(parentRequest, parentCancellation.Token);
        }
        await Approve();
        await coordinator.ChildFollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var leader = privateLeads ? parentPending! : ordinaryPending!;
        if (cancelLeader)
        {
            (privateLeads ? parentCancellation : ordinaryCancellation).Cancel();
            Assert.IsType<AccessCancelled<Result>>(
                await leader.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await (privateLeads ? privateDetached : ordinaryDetached).Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        else
        {
            await broker.Policy.ForgetAsync(
                (privateLeads ? parentAuthorization : ordinaryAuthorization)!.MemoryIdentifier!,
                TestContext.Current.CancellationToken);
        }
        if (revokeSurvivor)
        {
            await broker.Policy.ForgetAsync(
                (privateLeads ? ordinaryAuthorization : parentAuthorization)!.MemoryIdentifier!,
                TestContext.Current.CancellationToken);
        }
        proceed.TrySetResult();
        var survivor = privateLeads ? ordinaryPending! : parentPending!;
        var survived = await survivor.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (revokeSurvivor)
        {
            Assert.IsNotType<AccessSucceeded<Result>>(survived);
        }
        else
        {
            await using var result = Assert.IsType<AccessSucceeded<Result>>(survived).Result;
        }
        if (!cancelLeader)
        {
            Assert.IsNotType<AccessSucceeded<Result>>(
                await leader.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        Assert.Equal(1, providerExecutions);
        Assert.Equal(revokeSurvivor ? 0 : 1, publications);
        Assert.Equal(!revokeSurvivor && !privateLeads ? 1 : 0, parentMaterializations);
        Assert.Equal(!revokeSurvivor && privateLeads ? 1 : 0, ordinaryMaterializations);

        Task Approve()
        {
            var row = registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending);
            return registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Source = row.SelectedSource,
                Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
            }, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 0)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 0)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 1)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 1)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 2)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 2)]
    [InlineData(ReleaseMemoryScopeKind.ExactUse, 3)]
    [InlineData(ReleaseMemoryScopeKind.AllUses, 3)]
    public async Task AccessBroker_SelectedSourceAccount_DoesNotBorrowAnotherCandidatesCommonIdentity(
        ReleaseMemoryScopeKind scope, int sourceIdentity)
    {
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "environment-a");
        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "cli-b");
        var expectedAccount = sourceIdentity == 0 ? null : accountA;
        var commonAccount = sourceIdentity is 2 or 3 ? accountA : accountB;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReleaseAuthorization? committed = null;
        ICredentialExecutionContext? executionContext = null;
        var request = Request();
        var environment = request.Sources[0] with
        {
            Provider = CredentialProviderKind.GitHub,
            Kind = CredentialSourceKind.Environment,
            Usability = CredentialSourceUsability.Missing,
        };
        var cli = environment with
        {
            Identifier = new() { Digest = new string('c', 64) },
            Kind = CredentialSourceKind.GitHubCli,
            Usability = CredentialSourceUsability.Usable,
        };
        request = request with
        {
            Credential = request.Credential with
            {
                Provider = CredentialProviderKind.GitHub,
                Kind = CredentialKind.AccessToken,
                AccountIdentifier = sourceIdentity == 2 ? accountA : null,
            },
            Use = request.Use with { MemoryScopes = [scope, ReleaseMemoryScopeKind.AllowOnce] },
            Sources = sourceIdentity == 3 ? [environment] : [environment, cli],
        };
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.GitHub,
            InspectOverride = candidate => new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = candidate.Credential with { AccountIdentifier = commonAccount },
                Sources = candidate.Sources,
                CachedGenerations = sourceIdentity == 3
                    ? new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>()
                    : new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>
                    {
                        [cli.Identifier] = new()
                        {
                            Generation = new() { Value = Guid.NewGuid() },
                            AccountIdentifier = accountB,
                        },
                    },
                AcquisitionKeys = candidate.Sources.ToDictionary(source => source.Identifier, source => new ProviderAcquisitionKey
                {
                    Configuration = candidate.Credential.LogicalIdentifier,
                    Source = source.Identifier,
                    Account = source.Identifier == environment.Identifier ? expectedAccount : accountB,
                    Protocol = ProviderProtocolKind.Cli,
                }),
            },
            Execute = async (_, inspection, authorization, admission, context, cancellationToken) =>
            {
                Assert.Equal(commonAccount, inspection.Credential.AccountIdentifier);
                Assert.Equal(environment.Identifier, inspection.SelectedSource);
                var key = inspection.AcquisitionKeys[environment.Identifier];
                Assert.Equal(expectedAccount, key.Account);
                Assert.True(admission!.Authorizes(context.RequestIdentifier, context.Revision,
                    CredentialExecutorKind.GitHub, context.AcquisitionGeneration, key));
                committed = authorization;
                executionContext = context;
                entered.TrySetResult();
                await failProvider.Task.WaitAsync(cancellationToken);
                return new CredentialExecutionMissing<Result>();
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Source = environment.Identifier,
            Memory = row.MemoryOptions.Single(option => option.Scope == scope).Identifier,
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(expectedAccount, executionContext!.PolicyRequest!.Credential.AccountIdentifier);
        Assert.Equal(ReleaseAuthorizationValidation.Valid,
            await ((IProviderAcquisitionAuthorizationPolicy)broker.Policy).ValidateAcquisitionAuthorizationAsync(
                committed!, executionContext.PolicyRequest, TestContext.Current.CancellationToken));
        var remembered = await broker.Policy.ListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(remembered, memory =>
            memory.SourceIdentifier == environment.Identifier && memory.AccountIdentifier == accountB);
        if (expectedAccount is null)
        {
            Assert.Empty(remembered);
            Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch, await broker.Policy.ValidateAsync(
                committed!.Identifier, executionContext.PolicyRequest, TestContext.Current.CancellationToken));
        }
        else
        {
            var memory = Assert.Single(remembered);
            Assert.Equal(environment.Identifier, memory.SourceIdentifier);
            Assert.Equal(accountA, memory.AccountIdentifier);
            Assert.Equal(scope, memory.Scope);
        }
        Assert.False(pending.IsCompleted);
        failProvider.TrySetResult();
        Assert.IsType<AccessReacquisitionRequired<Result>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(expectedAccount is null ? 0 : 1, (await broker.Policy.ListAsync(TestContext.Current.CancellationToken)).Count);
    }

    private static RequestRecord ConnectRequest(CredentialAccountIdentifier? account)
    {
        var original = Request();
        return original with
        {
            Credential = original.Credential with
            {
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                AccountIdentifier = account,
            },
            Sources = original.Sources.Select(source => source with { Provider = CredentialProviderKind.DevTunnel }).ToArray(),
        };
    }

    private (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(
        Executor executor,
        IProviderAcquisitionCoordinator? coordinator = null,
        IdentityExecutor? identityExecutor = null)
    {
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, $"{Guid.NewGuid():N}.json")));
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<RequestRecord, Result>(executor);
        if (identityExecutor is not null)
        {
            executors.Add<IdentityRequestRecord, Result>(identityExecutor);
        }
        var registry = new UserAuthorizationRegistry();
        return (new AccessBroker(policy, executors, coordinator ?? new ProviderAcquisitionCoordinator(), registry), registry);
    }

    private static Task ApplyAsync(
        UserAuthorizationRegistry registry,
        UserAuthorizationSnapshot row) => registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SelectedSource,
        }, TestContext.Current.CancellationToken);

    private static RequestRecord Request(string purpose = "first-use")
    {
        var policyRequest = ReleaseAuthorizationContractTests.Request();
        return new RequestRecord
        {
            Credential = policyRequest.Credential with { Provider = CredentialProviderKind.StoredSecret },
            Requestor = new()
            {
                Identifier = policyRequest.RequestorIdentifier,
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            },
            Use = policyRequest.Use with
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                    policyRequest.Use.Kind, policyRequest.Use.Lineage, policyRequest.RequestorIdentifier, purpose),
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = new[]
            {
                new CredentialSourceChoiceDescriptor
                {
                    Identifier = policyRequest.SourceIdentifier,
                    Kind = CredentialSourceKind.Provider,
                    Provider = CredentialProviderKind.StoredSecret,
                    Usability = CredentialSourceUsability.AcquisitionRequired,
                    SafeDisplayName = "Credential provider",
                },
            },
        };
    }

    private sealed record RequestRecord : CredentialAccessRequest<Result>;
    private sealed record IdentityRequestRecord : CredentialAccessRequest<Result>;

    private sealed class IdentityExecutor(
        ProviderAcquisitionKey key,
        Func<ICredentialExecutionContext, ReleaseAuthorization, ProviderInteractionPermit, CancellationToken,
            ValueTask<CredentialExecutionOutcome<Result>>> execute)
        : ICredentialAccessExecutor<IdentityRequestRecord, Result>
    {
        public CredentialExecutorKind Kind => CredentialExecutorKind.Entra;
        public ValueTask<CredentialInspection> InspectAsync(IdentityRequestRecord request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = request.Sources,
                AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey> { [key.Source] = key },
            });

        public ValueTask<CredentialExecutionOutcome<Result>> ExecuteAsync(
            IdentityRequestRecord request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken) => execute(context, authorization, interactionPermit!, cancellationToken);
    }

    private sealed class FaultingCoordinator : IProviderAcquisitionCoordinator
    {
        public int DisposalCount { get; private set; }
        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Must not execute.");

        public ValueTask DisposeAsync()
        {
            this.DisposalCount++;
            return ValueTask.FromException(new InvalidOperationException("coordinator"));
        }
    }

    private sealed class Result : ICredentialAccessResult
    {
        public SensitiveValueLease? Lease { get; init; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            if (this.Lease is not null)
            {
                await this.Lease.DisposeAsync();
            }

            this.Disposed.TrySetResult();
        }
    }

    private sealed class FaultingMaterializer(Result result) : IAccessResultMaterializer<Result>
    {
        public ValueTask<Result> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(result);
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Materializer cleanup failed.");
    }

    private sealed class GenerationValidator : ICredentialGenerationValidator
    {
        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed record CompletionRequest : CompletionAuthorizationRequest<CompletionResult>;

    private sealed class CompletionResult(TaskCompletionSource disposed) : IAccessResult
    {
        public ValueTask DisposeAsync()
        {
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompletionExecutor : ICompletionAuthorizationExecutor<CompletionRequest, CompletionResult>
    {
        private bool completed;
        public TaskCompletionSource UnclaimedDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions { get; private set; }
        public CredentialExecutorKind Kind => CredentialExecutorKind.CliOwned;
        public ValueTask<CompletionAuthorizationInspection> InspectAsync(
            CompletionRequest request,
            CancellationToken cancellationToken)
        {
            var source = UserAuthorizationRegistryTests.Snapshot().SourceOptions[0];
            return ValueTask.FromResult(new CompletionAuthorizationInspection
            {
                IsCompleted = this.completed,
                Source = source,
                AcquisitionKey = new()
                {
                    Configuration = new() { Digest = new string('a', 64) },
                    Source = source.Identifier,
                    Protocol = ProviderProtocolKind.Cli,
                },
            });
        }

        public ValueTask<CompletionResult> CreateCompletedResultAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CompletionResult(this.UnclaimedDisposed));

        public ValueTask<CredentialExecutionOutcome<CompletionResult>> ExecuteAsync(
            CompletionRequest request,
            ProviderInteractionPermit permit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken)
        {
            Assert.Null(context.PolicyRequest);
            Assert.Throws<InvalidOperationException>(() => context.CreateLeaseContext(new GenerationValidator()));
            this.Executions++;
            this.completed = this.Executions > 1;
            return ValueTask.FromResult<CredentialExecutionOutcome<CompletionResult>>(!this.completed
                ? new CredentialExecutionFailed<CompletionResult> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = true }
                : new CredentialExecutionReady<CompletionResult> { Materializer = new CompletionMaterializer(this.UnclaimedDisposed) });
        }
    }

    private sealed class CompletionMaterializer(TaskCompletionSource disposed) : IAccessResultMaterializer<CompletionResult>
    {
        public ValueTask<CompletionResult> MaterializeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CompletionResult(disposed));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Materializer(Func<Result> materialize) : IAccessResultMaterializer<Result>
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<Result> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(materialize());
        public ValueTask DisposeAsync()
        {
            this.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Executor : ICredentialAccessExecutor<RequestRecord, Result>, IAsyncDisposable
    {
        public required Func<RequestRecord, CredentialInspection, ReleaseAuthorization, ProviderInteractionPermit?, ICredentialExecutionContext,
            CancellationToken, ValueTask<CredentialExecutionOutcome<Result>>> Execute { get; init; }
        public Func<bool> IsUsable { get; init; } = () => false;
        public Func<CredentialInspection, CredentialInspection> RefineInspection { get; init; } = inspection => inspection;
        public Func<RequestRecord, CredentialInspection>? InspectOverride { get; init; }
        public CredentialExecutorKind Kind { get; init; } = CredentialExecutorKind.OAuth;
        public Func<ValueTask> Dispose { get; init; } = () => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => this.Dispose();

        public ValueTask<CredentialInspection> InspectAsync(
            RequestRecord request,
            CancellationToken cancellationToken)
        {
            if (this.InspectOverride is { } inspect)
            {
                return ValueTask.FromResult(inspect(request));
            }
            var source = request.Sources[0] with
            {
                Usability = this.IsUsable() ? CredentialSourceUsability.Usable : CredentialSourceUsability.AcquisitionRequired,
            };
            return ValueTask.FromResult(this.RefineInspection(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = new[] { source },
                AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
                {
                    [source.Identifier] = new()
                    {
                        Configuration = request.Credential.LogicalIdentifier,
                        Source = source.Identifier,
                        Account = request.Credential.AccountIdentifier,
                        Protocol = this.Kind == CredentialExecutorKind.DevTunnelConnect
                            ? ProviderProtocolKind.TunnelConnect
                            : ProviderProtocolKind.OAuth,
                    },
                },
            }));
        }

        public ValueTask<CredentialExecutionOutcome<Result>> ExecuteAsync(
            RequestRecord request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken) =>
            this.Execute(request, inspection, authorization, interactionPermit, context, cancellationToken);
    }

    private sealed class ParticipantTrackingCoordinator : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int participants;
        private int childParticipants;
        internal TaskCompletionSource FollowerJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AnotherFollowerJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ChildFollowerJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var pending = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (operation.ExecutorKind == CredentialExecutorKind.DevTunnelConnect)
            {
                var joined = Interlocked.Increment(ref this.participants);
                if (joined == 2)
                {
                    this.FollowerJoined.TrySetResult();
                }
                if (joined == 3)
                {
                    this.AnotherFollowerJoined.TrySetResult();
                }
            }
            if (operation.ExecutorKind == CredentialExecutorKind.Entra &&
                Interlocked.Increment(ref this.childParticipants) == 2)
            {
                this.ChildFollowerJoined.TrySetResult();
            }

            return pending;
        }

        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    private sealed class GroupOperation(
        ICredentialExecutionContext context,
        ProviderAcquisitionKey key,
        Func<ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>> execute,
        CredentialExecutorKind executorKind = CredentialExecutorKind.DevTunnelConnect)
        : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => executorKind;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => execute(commitContext, cancellationToken);
    }

    private sealed class Operation(
        ICredentialExecutionContext context,
        ProviderAcquisitionKey key,
        Func<ProviderAcquisitionCommitContext, ValueTask<ProviderAcquisitionOutcome>> execute,
        CredentialExecutorKind executorKind = CredentialExecutorKind.OAuth) : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => executorKind;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => execute(commitContext);
    }
}
