using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Services.Access;

internal sealed class AccessBroker : IAccessBroker
{
    private readonly CredentialAccessExecutorRegistry executors;
    private readonly UserAuthorizationRegistry registry;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifetimeGate = new();
    private readonly ConcurrentDictionary<UserAuthorizationRequestIdentifier, IPendingInspection> pendingInspections = new();
    private readonly ConcurrentDictionary<UserAuthorizationRequestIdentifier, ExecutionGeneration> activeExecutions = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    internal AccessBroker(
        IReleaseAuthorizationPolicy policy,
        CredentialAccessExecutorRegistry executors,
        IProviderAcquisitionCoordinator coordinator,
        UserAuthorizationRegistry registry)
    {
        this.Policy = policy;
        this.executors = executors;
        this.Coordinator = coordinator;
        this.registry = registry;
    }

    internal IReleaseAuthorizationPolicy Policy { get; }
    internal IProviderAcquisitionCoordinator Coordinator { get; }

    public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
        AccessRequest<TResult> request,
        CancellationToken cancellationToken = default)
        where TResult : IAccessResult => this.AcquireNestedAsync(request, Array.Empty<CredentialDependencyKey>(), cancellationToken);

    internal async Task<AccessOutcome<TResult>> AcquireNestedAsync<TResult>(
        AccessRequest<TResult> request,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken)
        where TResult : IAccessResult
    {
        CancellationTokenSource cancellation;
        lock (this.lifetimeGate)
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                return new AccessShuttingDown<TResult>();
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        }

        using var ownedCancellation = cancellation;
        var task = this.DispatchSafeAsync(request, chain, cancellation.Token);
        try
        {
            return await task.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ObserveLate(DisposeUnclaimedAsync(task));
            return Volatile.Read(ref this.disposed) != 0 ? new AccessShuttingDown<TResult>() : new AccessCancelled<TResult>();
        }
    }

    private async Task<AccessOutcome<TResult>> DispatchSafeAsync<TResult>(
        AccessRequest<TResult> request,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken)
        where TResult : IAccessResult
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await this.executors.DispatchAsync(this, request, chain, cancellationToken).ConfigureAwait(false);
        }
        catch (ReleaseIdentityMismatchException)
        {
            return Failure<TResult>(AccessErrorCode.IdentityMismatch);
        }
        catch (IOException)
        {
            return Failure<TResult>(AccessErrorCode.PersistenceFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<TResult>(AccessErrorCode.PersistenceFailure);
        }
        catch (JsonException)
        {
            return Failure<TResult>(AccessErrorCode.PersistenceFailure);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref this.disposed) != 0)
        {
            return new AccessShuttingDown<TResult>();
        }
    }

    internal async Task<AccessOutcome<TResult>> AcquireCredentialAsync<TRequest, TResult>(
        TRequest request,
        ICredentialAccessExecutor<TRequest, TResult> executor,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        var inspection = await executor.InspectAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidInspection(request, inspection, request.Credential) || inspection.Sources.Count == 0)
        {
            return Failure<TResult>(AccessErrorCode.IdentityMismatch);
        }

        var identifier = new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() };
        var pinnedCredential = inspection.Credential;
        CredentialSourceChoiceDescriptor selected = inspection.Sources[0];
        ReleaseMemoryScopeKind? selectedMemory = null;
        foreach (var source in inspection.Sources)
        {
            var policyRequest = PolicyRequest(request, inspection, source.Identifier, identifier, 1);
            var evaluation = await this.Policy.EvaluateAsync(policyRequest, cancellationToken).ConfigureAwait(false);
            if (evaluation is ReleasePolicyMatched matched)
            {
                selected = source;
                selectedMemory = matched.Authorization.Scope;
                if (source.Usability == CredentialSourceUsability.Usable)
                {
                    var outcome = await this.ExecuteCredentialAsync(
                        request, executor, inspection, matched.Authorization, policyRequest, null, Guid.NewGuid(), chain, cancellationToken).ConfigureAwait(false);
                    if (outcome is not (AccessAuthorizationRequired<TResult> or AccessReacquisitionRequired<TResult>))
                    {
                        return outcome;
                    }

                    inspection = await executor.InspectAsync(request, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsValidInspection(request, inspection, pinnedCredential))
                    {
                        return Failure<TResult>(AccessErrorCode.IdentityMismatch);
                    }

                    inspection = PreserveSelectedSource(inspection, source);
                    selected = inspection.Sources.Single(option => option.Identifier == source.Identifier);
                    break;
                }
            }
        }

        return await this.StartCredentialRow(
            request, executor, inspection, selected.Identifier, identifier, chain, cancellationToken, selectedMemory).ConfigureAwait(false);
    }

    private Task<AccessOutcome<TResult>> StartCredentialRow<TRequest, TResult>(
        TRequest request,
        ICredentialAccessExecutor<TRequest, TResult> executor,
        CredentialInspection inspection,
        CredentialSourceIdentifier selectedSource,
        UserAuthorizationRequestIdentifier identifier,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken,
        ReleaseMemoryScopeKind? selectedMemory = null)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        this.registry.Add(CreateSnapshot(request, inspection.Sources, selectedSource, identifier, selectedMemory));
        var completion = new TaskCompletionSource<AccessOutcome<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingInspections[identifier] = new PendingInspection<TRequest, TResult>(request, executor, inspection.Credential);
        var worker = this.RunCredentialRowAsync(request, executor, inspection, identifier, chain, completion, cancellationToken);
        ObserveLate(worker);
        return completion.Task;
    }

    private async Task RunCredentialRowAsync<TRequest, TResult>(
        TRequest request,
        ICredentialAccessExecutor<TRequest, TResult> executor,
        CredentialInspection initialInspection,
        UserAuthorizationRequestIdentifier identifier,
        IReadOnlyList<CredentialDependencyKey> chain,
        TaskCompletionSource<AccessOutcome<TResult>> completion,
        CancellationToken cancellationToken)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        using var rowCancellation = cancellation.Token.Register(() => this.registry.Cancel(identifier));
        long revision = 1;
        try
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var decision = await this.registry.WaitForDecisionAsync(identifier, cancellation.Token).ConfigureAwait(false);
                revision = decision.Revision;
                if (decision.State is UserAuthorizationState.Denied or UserAuthorizationState.Cancelled)
                {
                    completion.TrySetResult(decision.State == UserAuthorizationState.Denied
                        ? new AccessDenied<TResult> { Reason = AccessDenialReason.UserDenied } : new AccessCancelled<TResult>());
                    return;
                }

                AccessOutcome<TResult> outcome;
                IReadOnlyList<CredentialSourceChoiceDescriptor>? renewalSources = null;
                try
                {
                    var inspection = await executor.InspectAsync(request, cancellation.Token).ConfigureAwait(false);
                    var source = inspection.Sources.SingleOrDefault(option => option.Identifier == decision.Source);
                    if (!IsValidInspection(request, inspection, initialInspection.Credential))
                    {
                        outcome = Failure<TResult>(AccessErrorCode.IdentityMismatch);
                    }
                    else if (source is null)
                    {
                        var selected = initialInspection.Sources.SingleOrDefault(option => option.Identifier == decision.Source)
                            ?? request.Sources.Single(option => option.Identifier == decision.Source);
                        renewalSources = PreserveSelectedSource(inspection, selected).Sources;
                        outcome = new AccessReacquisitionRequired<TResult> { RequestIdentifier = identifier };
                    }
                    else if (source.Usability != CredentialSourceUsability.Usable &&
                        decision.PrimaryAction != UserAuthorizationPrimaryActionKind.Perform)
                    {
                        outcome = new AccessReacquisitionRequired<TResult> { RequestIdentifier = identifier };
                    }
                    else
                    {
                        var policyRequest = PolicyRequest(request, inspection, source.Identifier, identifier, revision);
                        var authorization = await this.Policy.CommitAsync(new ReleasePolicyDecision
                        {
                            Request = policyRequest,
                            Scope = decision.Memory,
                        }, cancellation.Token).ConfigureAwait(false);
                        ProviderInteractionPermit? permit = null;
                        var acquisitionGeneration = Guid.NewGuid();
                        if (decision.PrimaryAction == UserAuthorizationPrimaryActionKind.Perform &&
                            source.Usability != CredentialSourceUsability.Usable &&
                            inspection.AcquisitionKeys.TryGetValue(source.Identifier, out var key))
                        {
                            if (key.Source != source.Identifier ||
                                key.Configuration != inspection.Credential.LogicalIdentifier)
                            {
                                throw new InvalidOperationException("The executor did not declare the selected acquisition identity.");
                            }

                            permit = new ProviderInteractionPermit(
                                identifier, revision, executor.Kind, acquisitionGeneration, key,
                                () => this.IsCurrentExecution(identifier, decision.Revision, acquisitionGeneration),
                                new ProviderExecutionAuthorizationBinding
                                {
                                    Policy = this.Policy,
                                    Authorization = authorization,
                                    Request = policyRequest,
                                    AcquisitionKey = key,
                                });
                        }

                        outcome = await this.ExecuteCredentialAsync(
                            request, executor, inspection, authorization, policyRequest, permit, acquisitionGeneration, chain, cancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (ReleaseIdentityMismatchException)
                {
                    outcome = Failure<TResult>(AccessErrorCode.IdentityMismatch);
                }
                catch (IOException)
                {
                    outcome = Failure<TResult>(AccessErrorCode.PersistenceFailure);
                }
                catch (UnauthorizedAccessException)
                {
                    outcome = Failure<TResult>(AccessErrorCode.PersistenceFailure);
                }
                catch (JsonException)
                {
                    outcome = Failure<TResult>(AccessErrorCode.PersistenceFailure);
                }

                if (cancellation.IsCancellationRequested && outcome is AccessSucceeded<TResult> unpublished)
                {
                    await unpublished.Result.DisposeAsync().ConfigureAwait(false);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                var state = outcome switch
                {
                    AccessSucceeded<TResult> => UserAuthorizationState.Completed,
                    AccessDenied<TResult> => UserAuthorizationState.Denied,
                    AccessAuthorizationRequired<TResult> or AccessReacquisitionRequired<TResult> => UserAuthorizationState.RenewalNeeded,
                    _ => UserAuthorizationState.Failed,
                };
                var accepted = this.registry.Complete(identifier, revision, state);
                if (accepted && renewalSources is not null)
                {
                    var snapshot = this.registry.GetSnapshot().SingleOrDefault(snapshot => snapshot.Identifier == identifier);
                    if (snapshot is not null)
                    {
                        this.registry.UpdateSources(identifier, snapshot.Revision, renewalSources);
                    }
                }

                var awaitingRenewal = outcome is AccessReacquisitionRequired<TResult> { AwaitExplicitRenewal: true };
                if (!accepted || (!awaitingRenewal && !completion.TrySetResult(outcome)))
                {
                    if (outcome is AccessSucceeded<TResult> succeeded)
                    {
                        await succeeded.Result.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (state is UserAuthorizationState.Completed or UserAuthorizationState.Denied)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var snapshot = this.registry.GetSnapshot().SingleOrDefault(snapshot => snapshot.Identifier == identifier);
            if (snapshot is not null)
            {
                this.registry.Complete(identifier, snapshot.Revision, UserAuthorizationState.Cancelled);
            }
            completion.TrySetResult(Volatile.Read(ref this.disposed) != 0 ? new AccessShuttingDown<TResult>() : new AccessCancelled<TResult>());
        }
        catch (Exception exception)
        {
            this.registry.Complete(identifier, revision, UserAuthorizationState.Failed);
            completion.TrySetException(exception);
            throw;
        }
        finally
        {
            this.pendingInspections.TryRemove(identifier, out _);
        }
    }

    private async Task<AccessOutcome<TResult>> ExecuteCredentialAsync<TRequest, TResult>(
        TRequest request,
        ICredentialAccessExecutor<TRequest, TResult> executor,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ReleasePolicyRequest policyRequest,
        ProviderInteractionPermit? permit,
        Guid acquisitionGeneration,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        var context = new CredentialExecutionContext(
            this, chain, policyRequest.RequestIdentifier, policyRequest.RequestGeneration,
            acquisitionGeneration, request.Requestor, request.Use, request.PresentationScope,
            policyRequest, permit is not null,
            (action, token) => this.RequireCredentialAsync(
                request, executor, inspection, policyRequest.SourceIdentifier, authorization.Scope, chain, action, token));
        this.activeExecutions[context.RequestIdentifier] = new ExecutionGeneration
        {
            Revision = context.Revision,
            Generation = context.AcquisitionGeneration,
        };
        CredentialExecutionOutcome<TResult> outcome;
        try
        {
            outcome = await executor.ExecuteAsync(
                request, inspection with { SelectedSource = policyRequest.SourceIdentifier }, authorization, permit, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.activeExecutions.TryRemove(context.RequestIdentifier, out _);
        }
        if (outcome is not CredentialExecutionReady<TResult> ready)
        {
            return ExecutionOutcome<TResult>(outcome, policyRequest.RequestIdentifier);
        }

        var materializerHandedOff = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ready.ResolvedIdentity is { } identity)
            {
                await this.Policy.FinalizeCredentialIdentityAsync(authorization.Identifier, identity, cancellationToken).ConfigureAwait(false);
                policyRequest = policyRequest with { Credential = policyRequest.Credential with { AccountIdentifier = identity.AccountIdentifier } };
                context.PolicyRequest = policyRequest;
            }

            var validation = await this.Policy.ValidateAsync(authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
            if (validation != ReleaseAuthorizationValidation.Valid)
            {
                return validation == ReleaseAuthorizationValidation.IdentityMismatch
                    ? Failure<TResult>(AccessErrorCode.IdentityMismatch)
                    : new AccessAuthorizationRequired<TResult> { RequestIdentifier = policyRequest.RequestIdentifier };
            }

            materializerHandedOff = true;
            return await this.MaterializeAndPublishAsync(ready.Materializer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!materializerHandedOff)
            {
                await ready.Materializer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<UserAuthorizationRequestIdentifier> RequireCredentialAsync<TRequest, TResult>(
        TRequest request,
        ICredentialAccessExecutor<TRequest, TResult> executor,
        CredentialInspection previousInspection,
        CredentialSourceIdentifier selectedSource,
        ReleaseMemoryScopeKind selectedMemory,
        IReadOnlyList<CredentialDependencyKey> chain,
        CredentialLeaseRequiredActionKind action,
        CancellationToken cancellationToken)
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        var inspection = await executor.InspectAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidInspection(request, inspection, previousInspection.Credential))
        {
            throw new ReleaseIdentityMismatchException();
        }

        inspection = PreserveSelectedSource(
            inspection,
            previousInspection.Sources.Single(source => source.Identifier == selectedSource));
        var identifier = new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() };
        var task = this.StartCredentialRow(
            request, executor, inspection, selectedSource, identifier, chain, this.lifetime.Token, selectedMemory);
        ObserveLate(DisposeUnclaimedAsync(task));
        return identifier;
    }

    internal async Task<AccessOutcome<TResult>> AcquireCompletionAsync<TRequest, TResult>(
        TRequest request,
        ICompletionAuthorizationExecutor<TRequest, TResult> executor,
        IReadOnlyList<CredentialDependencyKey> chain,
        CancellationToken cancellationToken)
        where TRequest : CompletionAuthorizationRequest<TResult>
        where TResult : IAccessResult
    {
        var inspection = await executor.InspectAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.IsCompleted)
        {
            var result = await executor.CreateCompletedResultAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await result.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new AccessSucceeded<TResult> { Result = result };
        }

        var identifier = new UserAuthorizationRequestIdentifier { Value = Guid.NewGuid() };
        this.registry.Add(CreateSnapshot(request,
            new[] { inspection.Source with { Usability = CredentialSourceUsability.AcquisitionRequired } },
            inspection.Source.Identifier, identifier));
        var completion = new TaskCompletionSource<AccessOutcome<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveLate(this.RunCompletionRowAsync(request, executor, inspection, identifier, chain, completion, cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task RunCompletionRowAsync<TRequest, TResult>(
        TRequest request,
        ICompletionAuthorizationExecutor<TRequest, TResult> executor,
        CompletionAuthorizationInspection initialInspection,
        UserAuthorizationRequestIdentifier identifier,
        IReadOnlyList<CredentialDependencyKey> chain,
        TaskCompletionSource<AccessOutcome<TResult>> completion,
        CancellationToken cancellationToken)
        where TRequest : CompletionAuthorizationRequest<TResult>
        where TResult : IAccessResult
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        using var rowCancellation = cancellation.Token.Register(() => this.registry.Cancel(identifier));
        long revision = 1;
        try
        {
            while (true)
            {
                var decision = await this.registry.WaitForDecisionAsync(identifier, cancellation.Token).ConfigureAwait(false);
                revision = decision.Revision;
                if (decision.State is UserAuthorizationState.Denied or UserAuthorizationState.Cancelled)
                {
                    completion.TrySetResult(decision.State == UserAuthorizationState.Denied
                        ? new AccessDenied<TResult> { Reason = AccessDenialReason.UserDenied } : new AccessCancelled<TResult>());
                    return;
                }

                var inspection = await executor.InspectAsync(request, cancellation.Token).ConfigureAwait(false);
                if (inspection.Source.Identifier != initialInspection.Source.Identifier ||
                    inspection.AcquisitionKey != initialInspection.AcquisitionKey)
                {
                    completion.TrySetResult(Failure<TResult>(AccessErrorCode.IdentityMismatch));
                    this.registry.Complete(identifier, revision, UserAuthorizationState.Failed);
                    return;
                }

                AccessOutcome<TResult> outcome;
                if (inspection.IsCompleted)
                {
                    var result = await executor.CreateCompletedResultAsync(cancellation.Token).ConfigureAwait(false);
                    outcome = new AccessSucceeded<TResult> { Result = result };
                }
                else
                {
                    var generation = Guid.NewGuid();
                    var permit = new ProviderInteractionPermit(
                        identifier, revision, executor.Kind, generation, inspection.AcquisitionKey,
                        () => this.IsCurrentExecution(identifier, decision.Revision, generation));
                    var context = new CredentialExecutionContext(
                        this, chain, identifier, revision, generation,
                        request.Requestor, request.Use, request.PresentationScope, null, true, null);
                    this.activeExecutions[identifier] = new ExecutionGeneration { Revision = revision, Generation = generation };
                    CredentialExecutionOutcome<TResult> executed;
                    try
                    {
                        executed = await executor.ExecuteAsync(request, permit, context, cancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        this.activeExecutions.TryRemove(identifier, out _);
                    }
                    if (executed is CredentialExecutionReady<TResult> ready)
                    {
                        outcome = await this.MaterializeAndPublishAsync(ready.Materializer, cancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        outcome = ExecutionOutcome<TResult>(executed, identifier);
                    }
                }

                if (cancellation.IsCancellationRequested)
                {
                    if (outcome is AccessSucceeded<TResult> unpublished)
                    {
                        await unpublished.Result.DisposeAsync().ConfigureAwait(false);
                    }

                    cancellation.Token.ThrowIfCancellationRequested();
                }

                var state = outcome switch
                {
                    AccessSucceeded<TResult> => UserAuthorizationState.Completed,
                    AccessDenied<TResult> => UserAuthorizationState.Denied,
                    _ => UserAuthorizationState.Failed,
                };
                if (!this.registry.Complete(identifier, revision, state) || !completion.TrySetResult(outcome))
                {
                    if (outcome is AccessSucceeded<TResult> unclaimed)
                    {
                        await unclaimed.Result.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (state is UserAuthorizationState.Completed or UserAuthorizationState.Denied)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var snapshot = this.registry.GetSnapshot().SingleOrDefault(snapshot => snapshot.Identifier == identifier);
            if (snapshot is not null)
            {
                this.registry.Complete(identifier, snapshot.Revision, UserAuthorizationState.Cancelled);
            }

            completion.TrySetResult(Volatile.Read(ref this.disposed) != 0 ? new AccessShuttingDown<TResult>() : new AccessCancelled<TResult>());
        }
        catch (Exception exception)
        {
            this.registry.Complete(identifier, revision, UserAuthorizationState.Failed);
            completion.TrySetException(exception);
            throw;
        }
    }

    internal async ValueTask RefreshPendingSourcesAsync(CancellationToken cancellationToken)
    {
        foreach (var snapshot in this.registry.GetSnapshot().Where(snapshot => snapshot.CanAct))
        {
            if (this.pendingInspections.TryGetValue(snapshot.Identifier, out var pending))
            {
                var sources = await pending.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (!sources.Any(source => source.Identifier == snapshot.SelectedSource))
                {
                    sources = sources.Append(snapshot.SourceOptions.Single(source => source.Identifier == snapshot.SelectedSource)
                        with { Usability = CredentialSourceUsability.Missing }).ToArray();
                }

                this.registry.UpdateSources(snapshot.Identifier, snapshot.Revision, sources);
            }
        }
    }

    internal bool IsCurrentExecution(
        UserAuthorizationRequestIdentifier identifier,
        long revision,
        Guid generation) =>
        Volatile.Read(ref this.disposed) == 0 &&
        this.activeExecutions.TryGetValue(identifier, out var active) &&
        active.Revision == revision && active.Generation == generation &&
        this.registry.GetSnapshot().Any(snapshot => snapshot.Identifier == identifier &&
            snapshot.Revision == revision && snapshot.State == UserAuthorizationState.Performing);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        var failures = new List<Exception>();
        await CleanupAsync(() => new ValueTask(this.lifetime.CancelAsync()), failures).ConfigureAwait(false);
        await CleanupAsync(this.registry.DisposeAsync, failures).ConfigureAwait(false);
        await CleanupAsync(this.Coordinator.DisposeAsync, failures).ConfigureAwait(false);
        await CleanupAsync(this.executors.DisposeAsync, failures).ConfigureAwait(false);
        this.pendingInspections.Clear();
        this.activeExecutions.Clear();
        if (this.Policy is IAsyncDisposable disposablePolicy)
        {
            await CleanupAsync(disposablePolicy.DisposeAsync, failures).ConfigureAwait(false);
        }

        lock (this.lifetimeGate)
        {
            this.lifetime.Dispose();
        }
        if (failures.Count > 0)
        {
            var failure = new AggregateException("Access shutdown did not complete cleanly.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }

        this.disposal.TrySetResult();
    }

    private static async ValueTask CleanupAsync(Func<ValueTask> cleanup, List<Exception> failures)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    internal static AccessFailed<TResult> Failure<TResult>(
        AccessErrorCode code,
        bool canRetry = false)
        where TResult : IAccessResult => new()
        {
            ErrorCode = code,
            SafeMessage = code switch
            {
                AccessErrorCode.UnsupportedRequest => "The access request is not supported.",
                AccessErrorCode.DependencyCycle => "A credential dependency cycle was detected.",
                AccessErrorCode.PersistenceFailure => "Release authorization could not be persisted.",
                AccessErrorCode.IdentityMismatch => "The credential identity changed.",
                AccessErrorCode.InvalidGeneration => "The credential generation is no longer usable.",
                AccessErrorCode.Disposed => "The access service is disposed.",
                _ => "Credential acquisition failed.",
            },
            CanRetry = canRetry,
        };

    private async ValueTask<AccessOutcome<TResult>> MaterializeAndPublishAsync<TResult>(
        IAccessResultMaterializer<TResult> materializer,
        CancellationToken cancellationToken)
        where TResult : IAccessResult
    {
        TResult? result = default;
        var ownsResult = false;
        try
        {
            await using (materializer.ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = await materializer.MaterializeAsync(cancellationToken).ConfigureAwait(false);
                ownsResult = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref this.disposed) != 0)
            {
                return new AccessShuttingDown<TResult>();
            }

            var outcome = new AccessSucceeded<TResult> { Result = result! };
            ownsResult = false;
            return outcome;
        }
        finally
        {
            if (ownsResult)
            {
                await result!.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static AccessOutcome<TResult> ExecutionOutcome<TResult>(
        CredentialExecutionOutcome<TResult> outcome,
        UserAuthorizationRequestIdentifier identifier)
        where TResult : IAccessResult => outcome switch
        {
            CredentialExecutionDenied<TResult> => new AccessDenied<TResult> { Reason = AccessDenialReason.PolicyDenied },
            CredentialExecutionMissing<TResult> or CredentialExecutionInteractionRequired<TResult> =>
                new AccessReacquisitionRequired<TResult> { RequestIdentifier = identifier },
            CredentialExecutionRenewalRequired<TResult> =>
                new AccessReacquisitionRequired<TResult> { RequestIdentifier = identifier, AwaitExplicitRenewal = true },
            CredentialExecutionFailed<TResult> failed => Failure<TResult>(failed.ErrorCode, failed.CanRetry),
            _ => throw new InvalidOperationException("Unsupported executor outcome."),
        };

    private static ReleasePolicyRequest PolicyRequest<TResult>(
        CredentialAccessRequest<TResult> request,
        CredentialInspection inspection,
        CredentialSourceIdentifier source,
        UserAuthorizationRequestIdentifier identifier,
        long generation)
        where TResult : ICredentialAccessResult => new()
        {
            RequestIdentifier = identifier,
            RequestGeneration = generation,
            Credential = inspection.Credential with
            {
                AccountIdentifier = SelectedSourceAccount(request.Credential, inspection, source),
            },
            SourceIdentifier = source,
            RequestorIdentifier = request.Requestor.Identifier,
            Use = request.Use with { MemoryScopes = Array.AsReadOnly(request.Use.MemoryScopes.ToArray()) },
        };

    private static CredentialAccountIdentifier? SelectedSourceAccount(
        CredentialReference requested,
        CredentialInspection inspection,
        CredentialSourceIdentifier source)
    {
        if (requested.AccountIdentifier is { } explicitAccount)
        {
            return explicitAccount;
        }

        var hasCachedSource = inspection.CachedGenerations.TryGetValue(source, out var cached);
        if (cached?.AccountIdentifier is { } cachedAccount)
        {
            return cachedAccount;
        }

        if (inspection.AcquisitionKeys.TryGetValue(source, out var acquisition))
        {
            // A selected source's unknown identity is not another candidate's common account.
            return acquisition.Account;
        }

        return hasCachedSource ? null : inspection.Credential.AccountIdentifier;
    }

    private static bool SameCredential(
        CredentialReference expected,
        CredentialReference actual) =>
        expected.LogicalIdentifier == actual.LogicalIdentifier &&
        expected.Provider == actual.Provider && expected.Kind == actual.Kind &&
        (expected.AccountIdentifier is null || expected.AccountIdentifier == actual.AccountIdentifier);

    private static bool IsValidInspection<TResult>(
        CredentialAccessRequest<TResult> request,
        CredentialInspection inspection,
        CredentialReference pinnedCredential)
        where TResult : ICredentialAccessResult =>
        SameCredential(request.Credential, inspection.Credential) &&
        SameCredential(pinnedCredential, inspection.Credential) &&
        inspection.Sources.All(source => request.Sources.Any(allowed => allowed.Identifier == source.Identifier));

    private static CredentialInspection PreserveSelectedSource(
        CredentialInspection inspection,
        CredentialSourceChoiceDescriptor selected)
    {
        if (inspection.Sources.Any(source => source.Identifier == selected.Identifier))
        {
            return inspection;
        }

        return inspection with
        {
            Sources = inspection.Sources.Append(selected with { Usability = CredentialSourceUsability.Missing }).ToArray(),
            AcquisitionKeys = inspection.AcquisitionKeys.Where(entry => entry.Key != selected.Identifier)
                .ToDictionary(entry => entry.Key, entry => entry.Value),
            CachedGenerations = inspection.CachedGenerations.Where(entry => entry.Key != selected.Identifier)
                .ToDictionary(entry => entry.Key, entry => entry.Value),
        };
    }

    private static UserAuthorizationSnapshot CreateSnapshot<TResult>(
        AccessRequest<TResult> request,
        IReadOnlyList<CredentialSourceChoiceDescriptor> sources,
        CredentialSourceIdentifier selectedSource,
        UserAuthorizationRequestIdentifier identifier,
        ReleaseMemoryScopeKind? selectedMemory = null)
        where TResult : IAccessResult
    {
        var scopes = request is CompletionAuthorizationRequest<TResult>
            ? new[] { ReleaseMemoryScopeKind.AllowOnce } : request.Use.MemoryScopes;
        var memory = scopes.Select(scope => new ReleaseMemoryOptionDescriptor
        {
            Identifier = new() { Value = Guid.NewGuid() },
            Scope = scope,
            SafeDisplayName = scope.ToString(),
        }).ToArray();
        var selected = sources.Single(source => source.Identifier == selectedSource);
        return new UserAuthorizationSnapshot
        {
            Identifier = identifier,
            Revision = 1,
            UseDisplayName = request.Use.SafeDisplayName,
            State = UserAuthorizationState.Pending,
            PrimaryAction = selected.Usability == CredentialSourceUsability.Usable
                ? UserAuthorizationPrimaryActionKind.Allow : UserAuthorizationPrimaryActionKind.Perform,
            MemoryOptions = memory,
            SelectedMemory = memory.FirstOrDefault(option => option.Scope == selectedMemory)?.Identifier ?? memory[0].Identifier,
            SourceOptions = sources,
            SelectedSource = selectedSource,
            SafeStatus = "Authorization required.",
            IsBootstrapBlocking = request.PresentationScope == AccessPresentationScope.Bootstrap,
            CanAct = true,
        };
    }

    private static void ObserveLate(Task task) =>
        _ = task.ContinueWith(completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task DisposeUnclaimedAsync<TResult>(Task<AccessOutcome<TResult>> task)
        where TResult : IAccessResult
    {
        if (await task.ConfigureAwait(false) is AccessSucceeded<TResult> succeeded)
        {
            await succeeded.Result.DisposeAsync().ConfigureAwait(false);
        }
    }

    private interface IPendingInspection
    {
        ValueTask<IReadOnlyList<CredentialSourceChoiceDescriptor>> InspectAsync(CancellationToken cancellationToken);
    }

    private sealed record ExecutionGeneration
    {
        public required long Revision { get; init; }
        public required Guid Generation { get; init; }
    }

    private sealed class PendingInspection<TRequest, TResult>(
        TRequest request,
        ICredentialAccessExecutor<TRequest, TResult> executor,
        CredentialReference pinnedCredential) : IPendingInspection
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        public async ValueTask<IReadOnlyList<CredentialSourceChoiceDescriptor>> InspectAsync(CancellationToken cancellationToken)
        {
            var inspection = await executor.InspectAsync(request, cancellationToken).ConfigureAwait(false);
            return IsValidInspection(request, inspection, pinnedCredential)
                ? inspection.Sources
                : request.Sources.Select(source => source with { Usability = CredentialSourceUsability.Missing }).ToArray();
        }
    }
}
