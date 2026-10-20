using System.IO;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class SensitiveValueLeaseTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-lease-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public async Task SensitiveValueLease_UseAsync_ForgottenPolicy_ReturnsAuthorizationRequired()
    {
        var (binding, context) = await this.CreateAsync(ReleaseMemoryScopeKind.ExactUse);
        await using var lease = new SensitiveValueLease("original".AsMemory(), binding, context);
        Assert.Equal("original", Assert.IsType<SensitiveValueUseSucceeded<string>>(await lease.UseAsync(
            (value, _) => ValueTask.FromResult(value), TestContext.Current.CancellationToken)).Result);
        await context.Policy.ForgetAsync(Assert.Single(await context.Policy.ListAsync(TestContext.Current.CancellationToken)).Identifier, TestContext.Current.CancellationToken);
        Assert.IsType<SensitiveValueUseAuthorizationRequired<bool>>(await lease.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException("Material must not be released."), TestContext.Current.CancellationToken));
        Assert.Equal(CredentialLeaseRequiredActionKind.Authorization, context.RequiredAction);
    }

    [Fact]
    public async Task SensitiveValueLease_UseAsync_ExpiredGeneration_ReturnsReacquisitionRequiredWithoutProviderCall()
    {
        var (binding, context) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce);
        await using var expired = new SensitiveValueLease("old".AsMemory(), binding with { ExpiresOn = DateTimeOffset.MinValue }, context);
        Assert.IsType<SensitiveValueUseReacquisitionRequired<bool>>(await expired.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Equal(0, context.GenerationChecks);
        await using var rotated = new SensitiveValueLease("old".AsMemory(), binding, context);
        context.CurrentGeneration = false;
        Assert.IsType<SensitiveValueUseReacquisitionRequired<bool>>(await rotated.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Equal(CredentialLeaseRequiredActionKind.Reacquisition, context.RequiredAction);
    }

    [Fact]
    public async Task CredentialUseLease_DisposeAsync_MultipleCalls_IsIdempotent()
    {
        var (binding, context) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce);
        var session = new Session();
        var lease = new CredentialUseLease(session, binding, context);
        Assert.Equal(7, Assert.IsType<CredentialUseSucceeded<int>>(await lease.UseAsync(
            _ => ValueTask.FromResult(7), TestContext.Current.CancellationToken)).Result);
        context.CurrentGeneration = false;
        Assert.IsType<CredentialUseReacquisitionRequired<int>>(await lease.UseAsync(
            _ => ValueTask.FromResult(8), TestContext.Current.CancellationToken));
        Assert.Equal(1, session.Uses);
        await Task.WhenAll(lease.DisposeAsync().AsTask(), lease.DisposeAsync().AsTask());
        Assert.Equal(1, session.Disposals);
        Assert.IsType<CredentialUseFailed<int>>(await lease.UseAsync(
            _ => ValueTask.FromResult(9), TestContext.Current.CancellationToken));
        await using var sensitive = new SensitiveValueLease("secret".AsMemory(), binding, context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sensitive.UseAsync((_, _) => ValueTask.FromResult(0), cancellation.Token));
    }

    [Fact]
    public async Task SensitiveValueLease_ValidationFailure_IsSanitizedWithoutHidingCallbackFailures()
    {
        var (binding, context) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce);
        var policy = new Mock<IReleaseAuthorizationPolicy>(MockBehavior.Strict);
        policy.Setup(value => value.ValidateAsync(
            binding.AuthorizationIdentifier, binding.Request, It.IsAny<CancellationToken>()))
            .Throws(new IOException("private-token-and-path"));
        await using var failed = new SensitiveValueLease("secret".AsMemory(), binding, new Context(policy.Object));
        var outcome = Assert.IsType<SensitiveValueUseFailed<int>>(await failed.UseAsync<int>(
            (_, _) => throw new InvalidOperationException("Must not release material."), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private", outcome.SafeMessage);
        await using var valid = new SensitiveValueLease("secret".AsMemory(), binding, context);
        var callbackFailure = await Assert.ThrowsAsync<IOException>(async () => await valid.UseAsync<int>(
            (_, _) => throw new IOException("application failure"), TestContext.Current.CancellationToken));
        Assert.Equal("application failure", callbackFailure.Message);
        await valid.DisposeAsync();
        await valid.DisposeAsync();
        Assert.IsType<SensitiveValueUseFailed<int>>(await valid.UseAsync<int>(
            (_, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_ForgottenParent_PreventsChildMaterialValidation()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.ExactUse, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.ExactUse, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        var owner = new LeaseOwner(child);
        await using var view = CreateAuthorizedView(child, owner, parentBinding, parentContext);
        Assert.Equal(0, childContext.GenerationChecks);
        await parentContext.Policy.ForgetAsync(
            Assert.Single(await parentContext.Policy.ListAsync(TestContext.Current.CancellationToken)).Identifier,
            TestContext.Current.CancellationToken);

        var outcome = Assert.IsType<SensitiveValueUseAuthorizationRequired<bool>>(await view.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException("Neither material nor the operation may be accessed."),
            TestContext.Current.CancellationToken));

        Assert.Equal(parentBinding.Request.RequestIdentifier, outcome.RequestIdentifier);
        Assert.Equal(0, childContext.GenerationChecks);
        Assert.Null(childContext.RequiredAction);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_ForgottenChild_PreservesChildAuthorizationRow()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.ExactUse, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.ExactUse, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        await using var view = CreateAuthorizedView(child, new LeaseOwner(child), parentBinding, parentContext);
        await childContext.Policy.ForgetAsync(
            Assert.Single(await childContext.Policy.ListAsync(TestContext.Current.CancellationToken)).Identifier,
            TestContext.Current.CancellationToken);

        var outcome = Assert.IsType<SensitiveValueUseAuthorizationRequired<bool>>(await view.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException("Material must not be released."),
            TestContext.Current.CancellationToken));

        Assert.Equal(childBinding.Request.RequestIdentifier, outcome.RequestIdentifier);
        Assert.Equal(0, childContext.GenerationChecks);
        Assert.Null(parentContext.RequiredAction);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_UsesOnlyOriginalChildGeneration()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        await using var view = CreateAuthorizedView(child, new LeaseOwner(child), parentBinding, parentContext);
        Assert.Equal("original", Assert.IsType<SensitiveValueUseSucceeded<string>>(await view.UseAsync(
            (material, _) => ValueTask.FromResult(material),
            TestContext.Current.CancellationToken)).Result);
        childContext.CurrentGeneration = false;

        var outcome = Assert.IsType<SensitiveValueUseReacquisitionRequired<bool>>(await view.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException("A replacement generation must not be substituted."),
            TestContext.Current.CancellationToken));

        Assert.Equal(childBinding.Request.RequestIdentifier, outcome.RequestIdentifier);
        Assert.Equal(2, childContext.GenerationChecks);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_PreservesOriginalExpiryWithoutMaterialValidation()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding with { ExpiresOn = DateTimeOffset.MinValue }, childContext);
        await using var view = CreateAuthorizedView(child, new LeaseOwner(child), parentBinding, parentContext);

        var outcome = Assert.IsType<SensitiveValueUseReacquisitionRequired<bool>>(await view.UseAsync<bool>(
            (_, _) => throw new InvalidOperationException("Expired material must not be released."),
            TestContext.Current.CancellationToken));

        Assert.Equal(childBinding.Request.RequestIdentifier, outcome.RequestIdentifier);
        Assert.Equal(0, childContext.GenerationChecks);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_ExpiredChildWithForgottenPolicy_RequiresChildAuthorizationFirst()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.ExactUse, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding with { ExpiresOn = DateTimeOffset.MinValue }, childContext);
        await using var view = CreateAuthorizedView(child, new LeaseOwner(child), parentBinding, parentContext);
        await childContext.Policy.ForgetAsync(
            Assert.Single(await childContext.Policy.ListAsync(TestContext.Current.CancellationToken)).Identifier,
            TestContext.Current.CancellationToken);

        var outcome = Assert.IsType<SensitiveValueUseAuthorizationRequired<int>>(await view.UseAsync<int>(
            (_, _) => throw new InvalidOperationException("Neither expiry nor material may bypass child authorization."),
            TestContext.Current.CancellationToken));

        Assert.Equal(childBinding.Request.RequestIdentifier, outcome.RequestIdentifier);
        Assert.Equal(0, childContext.GenerationChecks);
        Assert.Null(parentContext.RequiredAction);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_MultipleCleanupFailures_ArePreserved()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        var cancellationFailure = new InvalidOperationException("Cancellation cleanup failed.");
        var ownerFailure = new IOException("Child cleanup failed.");
        var owner = new LeaseOwner(child, ownerFailure);
        var view = CreateAuthorizedView(child, owner, parentBinding, parentContext);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var use = view.UseAsync<int>(async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => throw cancellationFailure);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }, TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => view.DisposeAsync().AsTask());

        Assert.Contains(cancellationFailure, failure.Flatten().InnerExceptions);
        Assert.Contains(ownerFailure, failure.Flatten().InnerExceptions);
        Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => view.DisposeAsync().AsTask()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => use);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_CompletionOnlyContext_RejectsExportWithoutTakingOwnership()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        await using var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        var owner = new LeaseOwner(child);

        Assert.Throws<InvalidOperationException>(() => SensitiveValueLease.CreateAuthorizedView(
            child,
            owner,
            parentBinding.AuthorizationIdentifier,
            new ViewExecutionContext(parentBinding.Request, parentContext, completionOnly: true)));

        Assert.Equal(0, owner.Disposals);
        Assert.Equal(0, childContext.GenerationChecks);
        Assert.Equal("original", Assert.IsType<SensitiveValueUseSucceeded<string>>(await child.UseAsync(
            (material, _) => ValueTask.FromResult(material),
            TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_ChildDisposalFailure_IsPreservedWithoutRepeatedCleanup()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        var failure = new IOException("Child cleanup failed.");
        var owner = new LeaseOwner(child, failure);
        var view = CreateAuthorizedView(child, owner, parentBinding, parentContext);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => view.DisposeAsync().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => view.DisposeAsync().AsTask()));

        Assert.Equal(1, owner.Disposals);
        Assert.IsType<SensitiveValueUseFailed<int>>(await child.UseAsync(
            (_, _) => ValueTask.FromResult(1),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SensitiveValueLease_AuthorizedView_DisposalCancelsUseAndDisposesChildOwnerExactlyOnce()
    {
        var (childBinding, childContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "child");
        var (parentBinding, parentContext) = await this.CreateAsync(ReleaseMemoryScopeKind.AllowOnce, "parent");
        var child = new SensitiveValueLease("original".AsMemory(), childBinding, childContext);
        var owner = new LeaseOwner(child);
        var view = CreateAuthorizedView(child, owner, parentBinding, parentContext);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var use = view.UseAsync<int>(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }, TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(view.DisposeAsync().AsTask(), view.DisposeAsync().AsTask());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => use);
        Assert.Equal(1, owner.Disposals);
        Assert.IsType<SensitiveValueUseFailed<int>>(await child.UseAsync(
            (_, _) => ValueTask.FromResult(1),
            TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseFailed<int>>(await view.UseAsync(
            (_, _) => ValueTask.FromResult(1),
            TestContext.Current.CancellationToken));
    }

    private static SensitiveValueLease CreateAuthorizedView(
        SensitiveValueLease child,
        IAsyncDisposable owner,
        CredentialLeaseBinding parentBinding,
        Context parentContext)
    {
        return SensitiveValueLease.CreateAuthorizedView(
            child,
            owner,
            parentBinding.AuthorizationIdentifier,
            new ViewExecutionContext(parentBinding.Request, parentContext));
    }

    private async Task<(CredentialLeaseBinding Binding, Context Context)> CreateAsync(
        ReleaseMemoryScopeKind scope,
        string policyName = "memory")
    {
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, $"{policyName}.json")));
        var request = ReleaseAuthorizationContractTests.Request();
        request = request with { Credential = request.Credential with { Provider = CredentialProviderKind.StoredSecret } };
        var authorization = await policy.CommitAsync(new() { Request = request, Scope = scope }, TestContext.Current.CancellationToken);
        return (new CredentialLeaseBinding
        {
            AuthorizationIdentifier = authorization.Identifier,
            Request = request,
            MaterialGeneration = new() { Value = Guid.NewGuid() },
        }, new Context(policy));
    }

    private sealed class ViewExecutionContext(
        ReleasePolicyRequest request,
        Context parentContext,
        bool completionOnly = false) : ICredentialExecutionContext
    {
        public UserAuthorizationRequestIdentifier RequestIdentifier => request.RequestIdentifier;
        public long Revision => throw new NotSupportedException();
        public Guid AcquisitionGeneration => throw new NotSupportedException();
        public AccessRequestor Requestor => new()
        {
            Identifier = request.RequestorIdentifier,
            Kind = AccessRequestorKind.Application,
            SafeDisplayName = "Application",
        };
        public CredentialUseDescriptor Use => request.Use;
        public AccessPresentationScope PresentationScope => AccessPresentationScope.Runtime;
        public ReleasePolicyRequest? PolicyRequest => completionOnly ? null : request;
        public IReleaseAuthorizationPolicy Policy => parentContext.Policy;

        public ValueTask<AccessOutcome<TResult>> AcquireDependencyAsync<TResult>(
            AccessRequest<TResult> dependency,
            CancellationToken cancellationToken)
            where TResult : IAccessResult => throw new NotSupportedException();

        public ValueTask<ProviderAcquisitionOutcome> ExecuteProviderAcquisitionAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ProviderAcquisitionOutcome> ExecutePrivateProviderDependencyAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit parentGenerationPermit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ICredentialLeaseContext CreateLeaseContext(ICredentialGenerationValidator validator) =>
            new ViewContext(parentContext, validator);
    }

    private sealed class ViewContext(
        Context parentContext,
        ICredentialGenerationValidator validator) : ICredentialLeaseContext
    {
        public IReleaseAuthorizationPolicy Policy => parentContext.Policy;

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken) => validator.IsCurrentGenerationAsync(generation, cancellationToken);

        public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
            ReleasePolicyRequest request,
            CredentialLeaseRequiredActionKind action,
            CancellationToken cancellationToken) => parentContext.RequireAsync(request, action, cancellationToken);
    }

    private sealed class LeaseOwner(
        SensitiveValueLease lease,
        Exception? disposalFailure = null) : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public async ValueTask DisposeAsync()
        {
            this.Disposals++;
            await lease.DisposeAsync();
            if (disposalFailure is not null)
            {
                throw disposalFailure;
            }
        }
    }

    private sealed class Context(IReleaseAuthorizationPolicy policy) : ICredentialLeaseContext
    {
        public IReleaseAuthorizationPolicy Policy => policy;
        public bool CurrentGeneration { get; set; } = true;
        public int GenerationChecks { get; private set; }
        public CredentialLeaseRequiredActionKind? RequiredAction { get; private set; }
        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            this.GenerationChecks++;
            return ValueTask.FromResult(this.CurrentGeneration);
        }

        public ValueTask<UserAuthorizationRequestIdentifier> RequireAsync(
            ReleasePolicyRequest request,
            CredentialLeaseRequiredActionKind action,
            CancellationToken cancellationToken)
        {
            this.RequiredAction = action;
            return ValueTask.FromResult(request.RequestIdentifier);
        }
    }

    private sealed class Session : ICredentialUseSession
    {
        public int Uses { get; private set; }
        public int Disposals { get; private set; }
        public ValueTask<TResult> UseAsync<TResult>(
            Func<CancellationToken, ValueTask<TResult>> operation,
            CancellationToken cancellationToken)
        {
            this.Uses++;
            return operation(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            this.Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
