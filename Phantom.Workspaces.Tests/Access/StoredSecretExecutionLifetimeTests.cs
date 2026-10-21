using System.IO;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;

namespace Phantom.Workspaces.Tests.Access;

public sealed class StoredSecretExecutionLifetimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-stored-lifetime-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoredSecret_LateIgnoredCancellationRead_CannotCommitAfterHostDisposal()
    {
        var source = new DelayedSource();
        var state = await this.CreateAsync(source);
        await using var owned = state.Broker;
        var execution = state.ExecuteAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await state.Broker.DisposeAsync();
        var material = "late-test-material".ToCharArray();
        source.Complete(material);
        var outcome = await execution.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AccessErrorCode.Disposed, Assert.IsType<CredentialExecutionFailed<StoredSecretAccessResult>>(outcome).ErrorCode);
        Assert.Null(await state.Cache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.All(material, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task StoredSecret_LateIgnoredCancellationRead_CannotCommitAfterCallerCancellation()
    {
        var source = new DelayedSource();
        var state = await this.CreateAsync(source);
        await using var owned = state.Broker;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var execution = state.ExecuteAsync(cancellation.Token);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var material = "cancelled-test-material".ToCharArray();
        source.Complete(material);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Null(await state.Cache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.All(material, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task StoredSecret_ReleaseForgottenDuringRead_CannotCommitOrPublishMaterial()
    {
        var source = new DelayedSource();
        var state = await this.CreateAsync(source, ReleaseMemoryScopeKind.ExactUse);
        await using var owned = state.Broker;
        var execution = state.ExecuteAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await state.Policy.ForgetAsync(state.Authorization.MemoryIdentifier!, TestContext.Current.CancellationToken);
        var material = "revoked-test-material".ToCharArray();
        source.Complete(material);
        Assert.IsType<CredentialExecutionDenied<StoredSecretAccessResult>>(await execution.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Null(await state.Cache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.All(material, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task StoredSecret_DisposedDuringAsyncRevalidation_CannotCommitCapturedValidDecision()
    {
        var source = new DelayedSource();
        var state = await this.CreateAsync(source, pauseValidation: true);
        await using var owned = state.Broker;
        var execution = state.ExecuteAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var material = "validation-race-test-material".ToCharArray();
        source.Complete(material);
        var policy = Assert.IsType<PausingPolicy>(state.Policy);
        await policy.Validated.Task.WaitAsync(TestContext.Current.CancellationToken);
        await state.Broker.DisposeAsync();
        policy.Continue.SetResult();
        var outcome = await execution.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AccessErrorCode.Disposed, Assert.IsType<CredentialExecutionFailed<StoredSecretAccessResult>>(outcome).ErrorCode);
        Assert.Null(await state.Cache.InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.All(material, character => Assert.Equal('\0', character));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private async Task<State> CreateAsync(
        DelayedSource source,
        ReleaseMemoryScopeKind scope = ReleaseMemoryScopeKind.AllowOnce,
        bool pauseValidation = false)
    {
        var executor = new StoredSecretAccessExecutor(source);
        var request = StoredSecretAccessExecutorTests.Request();
        request = request with { Sources = [request.Sources[0]], Use = request.Use with { MemoryScopes = [scope] } };
        var inspection = await executor.InspectAsync(request, TestContext.Current.CancellationToken);
        var policyRequest = new ReleasePolicyRequest
        {
            RequestIdentifier = new() { Value = Guid.NewGuid() },
            RequestGeneration = 1,
            Credential = request.Credential,
            SourceIdentifier = request.Sources[0].Identifier,
            RequestorIdentifier = request.Requestor.Identifier,
            Use = request.Use,
        };
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json")));
        IReleaseAuthorizationPolicy effectivePolicy = pauseValidation ? new PausingPolicy(policy) : policy;
        var authorization = await effectivePolicy.CommitAsync(new ReleasePolicyDecision { Request = policyRequest, Scope = scope }, TestContext.Current.CancellationToken);
        var registrations = new CredentialAccessExecutorRegistry();
        registrations.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(executor);
        var broker = new AccessBroker(effectivePolicy, registrations, new ProviderAcquisitionCoordinator(), new UserAuthorizationRegistry());
        var context = new CredentialExecutionContext(
            broker, [], policyRequest.RequestIdentifier, policyRequest.RequestGeneration, Guid.NewGuid(),
            request.Requestor, request.Use, request.PresentationScope, policyRequest, false,
            (_, _) => throw new InvalidOperationException("This regression must not publish a credential lease."));
        return new State
        {
            Broker = broker,
            Policy = effectivePolicy,
            Authorization = authorization,
            Cache = executor.GetGenerationCache(policyRequest.SourceIdentifier),
            ExecuteAsync = cancellationToken => executor.ExecuteAsync(
                request, inspection with { SelectedSource = policyRequest.SourceIdentifier }, authorization,
                null, context, cancellationToken).AsTask(),
        };
    }

    private sealed record State
    {
        public required AccessBroker Broker { get; init; }
        public required IReleaseAuthorizationPolicy Policy { get; init; }
        public required ReleaseAuthorization Authorization { get; init; }
        public required ICredentialGenerationCache Cache { get; init; }
        public required Func<CancellationToken, Task<CredentialExecutionOutcome<StoredSecretAccessResult>>> ExecuteAsync { get; init; }
    }

    private sealed class DelayedSource : IStoredSecretAccessSource
    {
        private readonly TaskCompletionSource<char[]?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<bool> ExistsAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public async ValueTask<char[]?> ReadAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Started.TrySetResult();
            return await this.completion.Task;
        }

        public void Complete(char[] material) => this.completion.SetResult(material);
    }

    private sealed class PausingPolicy(ReleaseAuthorizationPolicy policy) : IReleaseAuthorizationPolicy, IAsyncDisposable
    {
        private int validations;
        public TaskCompletionSource Validated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken = default) =>
            policy.EvaluateAsync(request, cancellationToken);
        public ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken = default) =>
            policy.CommitAsync(decision, cancellationToken);

        public async ValueTask<ReleaseAuthorizationValidation> ValidateAsync(
            ReleaseAuthorizationIdentifier identifier,
            ReleasePolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            var validation = await policy.ValidateAsync(identifier, request, cancellationToken);
            if (Interlocked.Increment(ref this.validations) == 2)
            {
                this.Validated.TrySetResult();
                await this.Continue.Task.WaitAsync(cancellationToken);
            }
            return validation;
        }

        public ValueTask FinalizeCredentialIdentityAsync(ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken = default) =>
            policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) => policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) => policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => policy.DisposeAsync();
    }
}
