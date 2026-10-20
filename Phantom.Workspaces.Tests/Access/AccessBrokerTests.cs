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
        var materialGeneration = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
        var executor = new Executor
        {
            IsUsable = () => !perform,
            Execute = async (_, inspection, _, permit, context, cancellationToken) =>
            {
                savedContext = context;
                savedPermit = permit;
                savedKey = inspection.AcquisitionKeys[inspection.SelectedSource!];
                if (permit is not null)
                {
                    Assert.False(permit.AuthorizesProvider(CredentialExecutorKind.OAuth, savedKey));
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
                    context.RequestIdentifier, context.Revision, CredentialExecutorKind.OAuth,
                    context.AcquisitionGeneration, savedKey, () => true);
                var acquired = await context.ExecutePrivateProviderDependencyAsync(child, parentPermit, cancellationToken);
                if (perform)
                {
                    Assert.Equal(materialGeneration, Assert.IsType<ProviderAcquisitionSucceeded>(acquired).CommittedGeneration);
                }
                else
                {
                    Assert.IsType<ProviderAcquisitionDenied>(acquired);
                }

                return new CredentialExecutionReady<Result> { Materializer = new Materializer(() => new Result()) };
            },
        };
        var (broker, registry) = this.Create(executor);
        await using var owned = broker;
        var task = broker.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        await ApplyAsync(registry, Assert.Single(registry.GetSnapshot()));
        await using var result = Assert.IsType<AccessSucceeded<Result>>(await task).Result;
        Assert.Equal(perform ? 1 : 0, commits);
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

    private (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(Executor executor)
    {
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, $"{Guid.NewGuid():N}.json")));
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<RequestRecord, Result>(executor);
        var registry = new UserAuthorizationRegistry();
        return (new AccessBroker(policy, executors, new ProviderAcquisitionCoordinator(), registry), registry);
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
        public CredentialExecutorKind Kind => CredentialExecutorKind.OAuth;

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
                        Protocol = ProviderProtocolKind.OAuth,
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
