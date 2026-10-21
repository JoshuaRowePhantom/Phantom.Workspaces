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
                    foreach (var invalidChild in new[]
                    {
                        new Operation(context, key, _ => throw new InvalidOperationException("A private child cannot rejoin its parent Connect key."),
                            CredentialExecutorKind.Entra),
                        new Operation(context, childKey, _ => throw new InvalidOperationException("An identity child cannot become a Connect parent."),
                            CredentialExecutorKind.DevTunnelConnect),
                    })
                    {
                        Assert.IsType<ProviderAcquisitionDenied>(
                            await context.ExecutePrivateProviderDependencyAsync(invalidChild, active, cancellationToken));
                    }
                    Assert.Equal(ReleaseAuthorizationValidation.IdentityMismatch,
                        await ((IProviderAcquisitionAuthorizationPolicy)context.Policy).ValidateAcquisitionAuthorizationAsync(
                            authorization,
                            context.PolicyRequest! with { SourceIdentifier = new() { Digest = new string('f', 64) } },
                            cancellationToken));
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
        Assert.IsType<AccessAuthorizationRequired<Result>>(await pending);
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
                        await context.ExecutePrivateProviderDependencyAsync(child, groupProof, groupCancellation));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessBroker_PrivateChildQueuedStart_RejectsRevokedOrDisposedPolicyBeforeExecution(bool disposePolicy)
    {
        var innerPolicy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "queued.json")));
        var policy = new QueuedAcquisitionPolicy(innerPolicy);
        ReleaseAuthorization? authorization = null;
        var executions = 0;
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (_, inspection, grant, admission, context, cancellationToken) =>
            {
                authorization = grant;
                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var parent = new GroupOperation(context, key, async (commit, groupCancellation) =>
                {
                    var child = new Operation(context, key with
                    {
                        Configuration = new() { Digest = new string('e', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                    }, _ =>
                    {
                        executions++;
                        throw new InvalidOperationException("A queued child must revalidate before provider work.");
                    }, CredentialExecutorKind.Entra);
                    Assert.IsType<ProviderAcquisitionDenied>(await context.ExecutePrivateProviderDependencyAsync(
                        child, commit.InteractionPermit, groupCancellation));
                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                });
                Assert.NotNull(admission);
                Assert.IsType<ProviderAcquisitionSucceeded>(
                    await context.ExecuteProviderAcquisitionAsync(parent, admission, cancellationToken));
                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor, suppliedPolicy: policy);
        await using var owned = broker;
        var request = ConnectRequest(CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a"));
        var pending = broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await ApplyScopeAsync(registry, row, ReleaseMemoryScopeKind.AllUses);
        await policy.ChildQueued.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, executions);
        Assert.NotNull(authorization);
        if (disposePolicy)
        {
            await innerPolicy.DisposeAsync();
        }
        else
        {
            Assert.NotNull(authorization.MemoryIdentifier);
            await innerPolicy.ForgetAsync(authorization.MemoryIdentifier, TestContext.Current.CancellationToken);
        }

        policy.Continue.TrySetResult();
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (disposePolicy)
        {
            Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<Result>>(outcome).ErrorCode);
        }
        else
        {
            Assert.IsType<AccessAuthorizationRequired<Result>>(outcome);
        }

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task AccessBroker_PrivateChildQueuedStart_ValidFollowerSurvivesRevokedLeadingParent()
    {
        var innerPolicy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "follower.json")));
        var policy = new QueuedAcquisitionPolicy(innerPolicy);
        var coordinator = new ParticipantTrackingCoordinator();
        var account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, "principal-a");
        var firstRequest = ConnectRequest(account);
        var secondRequest = firstRequest with
        {
            Credential = firstRequest.Credential with { LogicalIdentifier = new() { Digest = new string('c', 64) } },
        };
        ReleaseAuthorization? firstAuthorization = null;
        var executions = 0;
        var childGeneration = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
        var executor = new Executor
        {
            Kind = CredentialExecutorKind.DevTunnelConnect,
            Execute = async (request, inspection, grant, admission, context, cancellationToken) =>
            {
                var first = request.Credential.LogicalIdentifier == firstRequest.Credential.LogicalIdentifier;
                if (first)
                {
                    firstAuthorization = grant;
                }

                var key = inspection.AcquisitionKeys[inspection.SelectedSource!];
                var parent = new GroupOperation(context, key, async (commit, groupCancellation) =>
                {
                    var child = new Operation(context, key with
                    {
                        Configuration = new() { Digest = new string('e', 64) },
                        Protocol = ProviderProtocolKind.Sdk,
                    }, async childCommit =>
                    {
                        executions++;
                        return await childCommit.CommitAsync(childGeneration, _ => ValueTask.CompletedTask);
                    }, CredentialExecutorKind.Entra);
                    var acquired = await context.ExecutePrivateProviderDependencyAsync(
                        child, commit.InteractionPermit, groupCancellation);
                    if (first)
                    {
                        Assert.IsType<ProviderAcquisitionDenied>(acquired);
                    }
                    else
                    {
                        Assert.Equal(childGeneration, Assert.IsType<ProviderAcquisitionSucceeded>(acquired).CommittedGeneration);
                    }

                    return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
                });
                Assert.NotNull(admission);
                Assert.IsType<ProviderAcquisitionSucceeded>(
                    await context.ExecuteProviderAcquisitionAsync(parent, admission, cancellationToken));
                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor, coordinator, policy);
        await using var owned = broker;
        var firstPending = broker.AcquireAsync(firstRequest, TestContext.Current.CancellationToken);
        await ApplyScopeAsync(registry, Assert.Single(registry.GetSnapshot()), ReleaseMemoryScopeKind.AllUses);
        await policy.ChildQueued.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var secondPending = broker.AcquireAsync(secondRequest, TestContext.Current.CancellationToken);
        await ApplyScopeAsync(registry, registry.GetSnapshot().Last(), ReleaseMemoryScopeKind.AllowOnce);
        await coordinator.ChildFollowerJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(firstAuthorization);
        Assert.NotNull(firstAuthorization.MemoryIdentifier);
        await innerPolicy.ForgetAsync(firstAuthorization.MemoryIdentifier, TestContext.Current.CancellationToken);
        policy.Continue.TrySetResult();
        Assert.IsType<AccessAuthorizationRequired<Result>>(
            await firstPending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await using var result = Assert.IsType<AccessSucceeded<Result>>(
            await secondPending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, executions);
    }

    private static Task ApplyScopeAsync(
        UserAuthorizationRegistry registry,
        UserAuthorizationSnapshot row,
        ReleaseMemoryScopeKind scope) => registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Source = row.SelectedSource,
            Memory = row.MemoryOptions.Single(option => option.Scope == scope).Identifier,
        }, TestContext.Current.CancellationToken);

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
        IReleaseAuthorizationPolicy? suppliedPolicy = null)
    {
        var policy = suppliedPolicy ??
            new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, $"{Guid.NewGuid():N}.json")));
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<RequestRecord, Result>(executor);
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

    private sealed class Executor : ICredentialAccessExecutor<RequestRecord, Result>
    {
        public required Func<RequestRecord, CredentialInspection, ReleaseAuthorization, ProviderInteractionPermit?, ICredentialExecutionContext,
            CancellationToken, ValueTask<CredentialExecutionOutcome<Result>>> Execute { get; init; }
        public Func<bool> IsUsable { get; init; } = () => false;
        public Func<CredentialInspection, CredentialInspection> RefineInspection { get; init; } = inspection => inspection;
        public CredentialExecutorKind Kind { get; init; } = CredentialExecutorKind.OAuth;

        public ValueTask<CredentialInspection> InspectAsync(
            RequestRecord request,
            CancellationToken cancellationToken)
        {
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
        private int children;
        internal TaskCompletionSource FollowerJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ChildFollowerJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var pending = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (operation.ExecutorKind == CredentialExecutorKind.DevTunnelConnect &&
                Interlocked.Increment(ref this.participants) == 2)
            {
                this.FollowerJoined.TrySetResult();
            }

            if (operation.ExecutorKind == CredentialExecutorKind.Entra &&
                Interlocked.Increment(ref this.children) == 2)
            {
                this.ChildFollowerJoined.TrySetResult();
            }

            return pending;
        }

        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    private sealed class QueuedAcquisitionPolicy(ReleaseAuthorizationPolicy policy) :
        IReleaseAuthorizationPolicy, IProviderAcquisitionAuthorizationPolicy, IAsyncDisposable
    {
        private int validations;
        internal TaskCompletionSource ChildQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReleaseAuthorizationValidation> ValidateAcquisitionAuthorizationAsync(
            ReleaseAuthorization authorization,
            ReleasePolicyRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref this.validations) == 2)
            {
                this.ChildQueued.TrySetResult();
                await this.Continue.Task.WaitAsync(cancellationToken);
            }

            return await ((IProviderAcquisitionAuthorizationPolicy)policy).ValidateAcquisitionAuthorizationAsync(
                authorization, request, cancellationToken);
        }

        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.EvaluateAsync(request, cancellationToken);
        public ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken = default) =>
            policy.CommitAsync(decision, cancellationToken);
        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
            ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.ValidateAsync(identifier, request, cancellationToken);
        public ValueTask FinalizeCredentialIdentityAsync(
            ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken = default) =>
            policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
            policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) =>
            policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => policy.DisposeAsync();
    }

    private sealed class GroupOperation(
        ICredentialExecutionContext context,
        ProviderAcquisitionKey key,
        Func<ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>> execute)
        : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.DevTunnelConnect;
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
