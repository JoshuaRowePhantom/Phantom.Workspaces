using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class EntraBrowserTokenAcquirerTests
{
    [Fact]
    public async Task EntraBrowserAdapter_ConcurrentAccountSelections_SerializeAndCommitMatchingPinnedTokens()
    {
        var configuration = EntraCredentialSessionTests.Request();
        var accountA = Record("home-account-a");
        var accountB = Record("home-account-b");
        var selected = 0;
        var selector = new ScriptedCredential
        {
            AuthenticateOperation = (_, _) => ValueTask.FromResult(Interlocked.Increment(ref selected) == 1 ? accountA : accountB),
            ReadToken = (_, _) => ValueTask.FromResult(Token("wrong-mutable-selector-token")),
        };
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionsSeen = new ConcurrentQueue<InteractiveBrowserCredentialOptions>();
        var adapter = new EntraCredentialSession.BrowserTokenAcquirer(selector, configuration, options =>
        {
            optionsSeen.Enqueue(options);
            var record = options.AuthenticationRecord;
            Assert.True(options.DisableAutomaticAuthentication);
            Assert.NotNull(record);
            return new ScriptedCredential
            {
                ReadToken = async (_, cancellationToken) =>
                {
                    if (ReferenceEquals(record, accountA))
                    {
                        startedA.TrySetResult();
                        await finishA.Task.WaitAsync(cancellationToken);
                        return Token("token-a");
                    }

                    Assert.Same(accountB, record);
                    startedB.TrySetResult();
                    await finishB.Task.WaitAsync(cancellationToken);
                    return Token("token-b");
                },
            };
        });
        await using var session = new EntraCredentialSession(configuration, selector, adapter);
        var requestA = Pin(configuration, accountA);
        var requestB = Pin(configuration, accountB);
        using var gateA = new SemaphoreSlim(1, 1);
        using var gateB = new SemaphoreSlim(1, 1);
        var first = session.ExecuteAsync(requestA, await CommitContextAsync(session, requestA, gateA), CancellationToken.None).AsTask();
        await startedA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = session.ExecuteAsync(requestB, await CommitContextAsync(session, requestB, gateB), CancellationToken.None).AsTask();
        Assert.False(second.IsCompleted);
        Assert.Equal(1, selector.AuthenticationCalls);
        Assert.Single(optionsSeen);
        finishA.TrySetResult();
        var committedA = Assert.IsType<ProviderAcquisitionSucceeded>(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        await startedB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(requestA.Credential.AccountIdentifier,
            (await session.InspectGenerationAsync(CancellationToken.None))!.AccountIdentifier);
        var exportA = EntraSessionTestAuthorization.Export(requestA);
        using (var material = await session.CopyGenerationAsync(
            committedA.CommittedGeneration, exportA.Authorization, exportA.Context, CancellationToken.None))
        {
            Assert.NotNull(material);
            Assert.Equal("token-a", new string(material!.Token));
        }
        finishB.TrySetResult();
        var committedB = Assert.IsType<ProviderAcquisitionSucceeded>(await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(requestB.Credential.AccountIdentifier,
            (await session.InspectGenerationAsync(CancellationToken.None))!.AccountIdentifier);
        var exportB = EntraSessionTestAuthorization.Export(requestB);
        using (var material = await session.CopyGenerationAsync(
            committedB.CommittedGeneration, exportB.Authorization, exportB.Context, CancellationToken.None))
        {
            Assert.NotNull(material);
            Assert.Equal("token-b", new string(material!.Token));
        }

        Assert.Null(await session.CopyGenerationAsync(
            committedA.CommittedGeneration, exportA.Authorization, exportA.Context, CancellationToken.None));
        Assert.Equal(0, selector.TokenCalls);
        Assert.Equal(2, selector.AuthenticationCalls);
        Assert.Equal(new[] { accountA, accountB }, optionsSeen.Select(options => options.AuthenticationRecord));
        Assert.All(optionsSeen, options =>
        {
            Assert.True(options.DisableAutomaticAuthentication);
            Assert.Equal(configuration.Client.Value, options.ClientId);
            Assert.Equal("organizations", options.TenantId);
            Assert.Equal(new Uri("https://login.microsoftonline.com/"), options.AuthorityHost);
            Assert.Equal("phantom-access-entra:" + configuration.CreateLogicalIdentifier().Digest,
                options.TokenCachePersistenceOptions!.Name);
        });
    }

    [Fact]
    public async Task EntraBrowserAdapter_PinnedTokenFailure_CannotAutomaticallySelectAnotherPrincipal()
    {
        var configuration = EntraCredentialSessionTests.Request();
        var account = Record("home-account-a");
        var selector = new ScriptedCredential
        {
            AuthenticateOperation = (_, _) => ValueTask.FromResult(account),
            ReadToken = (_, _) => ValueTask.FromResult(Token("different-principal-token")),
        };
        var automaticFallbacks = 0;
        var adapter = new EntraCredentialSession.BrowserTokenAcquirer(selector, configuration, options =>
        {
            Assert.Same(account, options.AuthenticationRecord);
            return new ScriptedCredential
            {
                ReadToken = (_, _) =>
                {
                    if (options.DisableAutomaticAuthentication)
                    {
                        throw new AuthenticationFailedException("Pinned account requires explicit authentication.");
                    }

                    Interlocked.Increment(ref automaticFallbacks);
                    return ValueTask.FromResult(Token("different-principal-token"));
                },
            };
        });
        await using var session = new EntraCredentialSession(configuration, selector, adapter);
        var request = Pin(configuration, account);
        using var gate = new SemaphoreSlim(1, 1);
        var failure = Assert.IsType<ProviderAcquisitionFailed>(await session.ExecuteAsync(
            request, await CommitContextAsync(session, request, gate), CancellationToken.None));
        Assert.Equal(AccessErrorCode.ProviderFailure, failure.ErrorCode);
        Assert.Equal(0, selector.TokenCalls);
        Assert.Equal(0, automaticFallbacks);
        Assert.Null(await session.InspectGenerationAsync(CancellationToken.None));
        Assert.Null((await session.InspectCacheAsync(request, CancellationToken.None)).Generation);
    }

    [Fact]
    public async Task EntraBrowserAdapter_SelectedAccountMismatch_DoesNotCommitUnderApprovedAccount()
    {
        var configuration = EntraCredentialSessionTests.Request();
        var approved = Record("home-account-a");
        var selected = Record("home-account-b");
        var selector = new ScriptedCredential { AuthenticateOperation = (_, _) => ValueTask.FromResult(selected) };
        var adapter = new EntraCredentialSession.BrowserTokenAcquirer(selector, configuration, options =>
        {
            Assert.Same(selected, options.AuthenticationRecord);
            Assert.True(options.DisableAutomaticAuthentication);
            return new ScriptedCredential { ReadToken = (_, _) => ValueTask.FromResult(Token("token-b")) };
        });
        await using var session = new EntraCredentialSession(configuration, selector, adapter);
        var request = Pin(configuration, approved);
        using var gate = new SemaphoreSlim(1, 1);
        var failure = Assert.IsType<ProviderAcquisitionFailed>(await session.ExecuteAsync(
            request, await CommitContextAsync(session, request, gate), CancellationToken.None));
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Null(await session.InspectGenerationAsync(CancellationToken.None));
        Assert.Equal(0, selector.TokenCalls);
    }

    [Fact]
    public async Task EntraBrowserAdapter_CancelledQueuedSelection_CannotMutateTheActivePrincipal()
    {
        var configuration = EntraCredentialSessionTests.Request();
        var accountA = Record("home-account-a");
        var accountB = Record("home-account-b");
        var selected = 0;
        var selector = new ScriptedCredential
        {
            AuthenticateOperation = (_, _) => ValueTask.FromResult(Interlocked.Increment(ref selected) == 1 ? accountA : accountB),
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new EntraCredentialSession.BrowserTokenAcquirer(selector, configuration, options =>
            new ScriptedCredential
            {
                ReadToken = async (_, cancellationToken) =>
                {
                    Assert.True(options.DisableAutomaticAuthentication);
                    if (ReferenceEquals(options.AuthenticationRecord, accountA))
                    {
                        started.TrySetResult();
                        await finish.Task.WaitAsync(cancellationToken);
                    }

                    return Token(options.AuthenticationRecord!.HomeAccountId);
                },
            });
        var first = adapter.AcquireAsync(CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var cancelled = adapter.AcquireAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, selector.AuthenticationCalls);
        finish.TrySetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(accountA.HomeAccountId, firstResult.Token.Token);
        Assert.Equal(Pin(configuration, accountA).Credential.AccountIdentifier, firstResult.AccountIdentifier);
        var next = await adapter.AcquireAsync(CancellationToken.None);
        Assert.Equal(accountB.HomeAccountId, next.Token.Token);
        Assert.Equal(Pin(configuration, accountB).Credential.AccountIdentifier, next.AccountIdentifier);
        Assert.Equal(0, selector.TokenCalls);
    }

    [Fact]
    public async Task EntraBrowserAdapter_MissingStablePrincipalEvidence_CannotObtainOrCommitToken()
    {
        var configuration = EntraCredentialSessionTests.Request();
        var selector = new ScriptedCredential { AuthenticateOperation = (_, _) => ValueTask.FromResult(Record(string.Empty)) };
        var adapter = new EntraCredentialSession.BrowserTokenAcquirer(
            selector, configuration, _ => throw new InvalidOperationException("A principal-less record must not reach token acquisition."));
        await using var session = new EntraCredentialSession(configuration, selector, adapter);
        using var gate = new SemaphoreSlim(1, 1);
        var failure = Assert.IsType<ProviderAcquisitionFailed>(await session.ExecuteAsync(
            configuration, await CommitContextAsync(session, configuration, gate), CancellationToken.None));
        Assert.Equal(AccessErrorCode.ProviderFailure, failure.ErrorCode);
        Assert.Null(await session.InspectGenerationAsync(CancellationToken.None));
        Assert.Equal(0, selector.TokenCalls);
    }

    private static AuthenticationRecord Record(string stableAccount)
    {
        var json = JsonSerializer.Serialize(new
        {
            username = "test@example.invalid",
            authority = "https://login.microsoftonline.com/",
            homeAccountId = stableAccount,
            tenantId = "test-tenant",
            clientId = "test-client",
            version = "1.0",
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream, CancellationToken.None);
    }

    private static EntraCredentialAccessRequest Pin(
        EntraCredentialAccessRequest configuration,
        AuthenticationRecord account) => configuration with
        {
            Credential = configuration.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.Entra, account.HomeAccountId),
            },
        };

    private static AccessToken Token(string value) => new(value, DateTimeOffset.UtcNow.AddHours(1));

    private static async Task<ProviderAcquisitionCommitContext> CommitContextAsync(
        IEntraCredentialSession session,
        EntraCredentialAccessRequest request,
        SemaphoreSlim gate)
    {
        var inspection = await session.InspectCacheAsync(request, CancellationToken.None);
        return new ProviderAcquisitionCommitContext(
            () => true, CancellationToken.None, gate,
            EntraCredentialSessionTests.Permit(inspection.AcquisitionKey).BindExecution(() => true));
    }

    private sealed class ScriptedCredential : InteractiveBrowserCredential
    {
        private int authenticationCalls;
        private int tokenCalls;
        internal int AuthenticationCalls => Volatile.Read(ref this.authenticationCalls);
        internal int TokenCalls => Volatile.Read(ref this.tokenCalls);
        internal Func<TokenRequestContext, CancellationToken, ValueTask<AuthenticationRecord>>? AuthenticateOperation { get; init; }
        internal Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>>? ReadToken { get; init; }

        public override Task<AuthenticationRecord> AuthenticateAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.authenticationCalls);
            return this.AuthenticateOperation is { } operation
                ? operation(requestContext, cancellationToken).AsTask()
                : throw new InvalidOperationException("Token credential cannot perform account selection.");
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.tokenCalls);
            return this.ReadToken is { } operation
                ? operation(requestContext, cancellationToken)
                : throw new InvalidOperationException("Unexpected token acquisition.");
        }
    }
}
