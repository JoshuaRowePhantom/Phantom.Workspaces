using System.IO;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
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

    private async Task<(CredentialLeaseBinding Binding, Context Context)> CreateAsync(ReleaseMemoryScopeKind scope)
    {
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json")));
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
