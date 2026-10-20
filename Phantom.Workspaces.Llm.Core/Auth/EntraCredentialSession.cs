using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Auth;

internal sealed record EntraCacheInspection
{
    public required LogicalCredentialIdentifier LogicalIdentifier { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public required ProviderAcquisitionKey AcquisitionKey { get; init; }
    public CredentialGenerationInspection? Generation { get; init; }
    public CredentialAccountIdentifier? AccountIdentifier { get; init; }
}

internal sealed class EntraCachedMaterial : IDisposable
{
    public required char[] Token { get; init; }
    public required CredentialGenerationInspection Inspection { get; init; }

    public EntraCachedMaterial Copy() => new()
    {
        Token = this.Token.ToArray(),
        Inspection = this.Inspection,
    };

    public void Dispose() => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.Token.AsSpan()));
}

internal sealed record EntraAcquiredToken
{
    public required AccessToken Token { get; init; }
    public required CredentialAccountIdentifier AccountIdentifier { get; init; }
}

internal interface IEntraTokenAcquirer
{
    ValueTask<EntraAcquiredToken> AcquireAsync(CancellationToken cancellationToken);
}

internal abstract record EntraGenerationUseOutcome<TResult>;

internal sealed record EntraGenerationUseSucceeded<TResult> : EntraGenerationUseOutcome<TResult>
{
    public required TResult Result { get; init; }
}

internal sealed record EntraGenerationUseUnavailable<TResult> : EntraGenerationUseOutcome<TResult>;

internal interface IEntraCredentialSession : ICredentialGenerationCache, IAsyncDisposable
{
    ValueTask<EntraCacheInspection> InspectCacheAsync(
        EntraCredentialAccessRequest request,
        CancellationToken cancellationToken);

    ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
        EntraCredentialAccessRequest request,
        ProviderAcquisitionCommitContext commitContext,
        CancellationToken cancellationToken);

    ValueTask<EntraCachedMaterial?> CopyGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken);

    ValueTask<EntraGenerationUseOutcome<TResult>> UseGenerationAsync<TResult>(
        CredentialMaterialGenerationIdentifier generation,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken);
}

internal sealed class EntraCredentialSession : IEntraCredentialSession
{
    private readonly EntraPinnedTokenProvider provider;
    private readonly IEntraTokenAcquirer acquirer;
    private readonly LogicalCredentialIdentifier logicalIdentifier;
    private readonly CredentialSourceIdentifier sourceIdentifier;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim useGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    internal EntraCredentialSession(
        EntraCredentialAccessRequest configuration,
        TokenCredential credential,
        IEntraTokenAcquirer acquirer,
        TimeProvider? timeProvider = null)
    {
        this.logicalIdentifier = configuration.CreateLogicalIdentifier();
        this.sourceIdentifier = configuration.CreateSourceIdentifier();
        this.acquirer = acquirer;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.provider = new EntraPinnedTokenProvider(
            credential,
            configuration.Scopes.Select(scope => scope.Value),
            this.timeProvider);
    }

    internal static IEntraCredentialSession Create(EntraCredentialAccessRequest configuration)
    {
        var credential = new InteractiveBrowserCredential(CreateBrowserOptions(configuration));
        return new EntraCredentialSession(
            configuration,
            credential,
            new BrowserTokenAcquirer(credential, configuration));
    }

    private static InteractiveBrowserCredentialOptions CreateBrowserOptions(
        EntraCredentialAccessRequest configuration,
        AuthenticationRecord? authenticationRecord = null)
    {
        if (!EntraInteractiveCredentialFactory.TryParseAuthority(
            configuration.Authority.Value.AbsoluteUri, out var authorityHost, out var tenantIdentifier))
        {
            throw new ArgumentException("An absolute Entra authority is required.");
        }

        return new InteractiveBrowserCredentialOptions
        {
            AuthorityHost = authorityHost,
            TenantId = tenantIdentifier,
            ClientId = configuration.Client.Value,
            AuthenticationRecord = authenticationRecord,
            DisableAutomaticAuthentication = authenticationRecord is not null,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "phantom-access-entra:" + configuration.CreateLogicalIdentifier().Digest,
            },
        };
    }

    public ValueTask<EntraCacheInspection> InspectCacheAsync(
        EntraCredentialAccessRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        this.ValidateConfiguration(request);
        var generation = this.provider.InspectBrokerGeneration();
        return ValueTask.FromResult(new EntraCacheInspection
        {
            LogicalIdentifier = this.logicalIdentifier,
            SourceIdentifier = this.sourceIdentifier,
            Generation = generation,
            AccountIdentifier = this.provider.InspectBrokerAccount(),
            AcquisitionKey = this.CreateAcquisitionKey(request),
        });
    }

    public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
        EntraCredentialAccessRequest request,
        ProviderAcquisitionCommitContext commitContext,
        CancellationToken cancellationToken)
    {
        this.ValidateConfiguration(request);
        if (Volatile.Read(ref this.disposed) != 0 ||
            !commitContext.InteractionPermit.AuthorizesProvider(CredentialExecutorKind.Entra, this.CreateAcquisitionKey(request)))
        {
            return new ProviderAcquisitionDenied();
        }

        try
        {
            // Authenticate/GetToken can both contact the provider. Neither is part of inspection.
            var acquired = await this.acquirer.AcquireAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (acquired.Token.ExpiresOn <= this.timeProvider.GetUtcNow() ||
                string.IsNullOrEmpty(acquired.Token.Token))
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }

            if (request.Credential.AccountIdentifier is { } expected && expected != acquired.AccountIdentifier)
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
            }

            using var material = new EntraCachedMaterial
            {
                Token = acquired.Token.Token.ToCharArray(),
                Inspection = new CredentialGenerationInspection
                {
                    Generation = new() { Value = Guid.NewGuid() },
                    ExpiresOn = acquired.Token.ExpiresOn,
                    AccountIdentifier = acquired.AccountIdentifier,
                },
            };
            return await this.provider.CommitBrokerGenerationAsync(material, commitContext).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException)
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
        catch (Azure.RequestFailedException)
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
    }

    public ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(this.provider.InspectBrokerGeneration());
    }

    public ValueTask<bool> IsCurrentGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(this.provider.IsCurrentBrokerGeneration(generation));
    }

    public ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(this.provider.InvalidateBrokerGeneration(generation));
    }

    public ValueTask<EntraCachedMaterial?> CopyGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(this.provider.CopyBrokerGeneration(generation));
    }

    public async ValueTask<EntraGenerationUseOutcome<TResult>> UseGenerationAsync<TResult>(
        CredentialMaterialGenerationIdentifier generation,
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return new EntraGenerationUseUnavailable<TResult>();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        await this.useGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            using var material = this.provider.CopyBrokerGeneration(generation);
            if (material is null || material.Inspection.ExpiresOn is not { } expiresOn ||
                this.timeProvider.GetUtcNow() >= expiresOn)
            {
                return new EntraGenerationUseUnavailable<TResult>();
            }

            var result = await operation(material.Token, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return new EntraGenerationUseSucceeded<TResult> { Result = result };
        }
        finally
        {
            this.useGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            try
            {
                await this.lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await this.useGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    this.provider.DisposeBrokerCache();
                }
                finally
                {
                    this.useGate.Release();
                }
            }

            this.disposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.disposal.TrySetException(exception);
            throw;
        }
    }

    private ProviderAcquisitionKey CreateAcquisitionKey(EntraCredentialAccessRequest request) => new()
    {
        Configuration = this.logicalIdentifier,
        Source = this.sourceIdentifier,
        Account = request.Credential.AccountIdentifier ?? this.provider.InspectBrokerAccount(),
        Protocol = ProviderProtocolKind.Sdk,
    };

    private void ValidateConfiguration(EntraCredentialAccessRequest request)
    {
        if (request.CreateLogicalIdentifier() != this.logicalIdentifier ||
            request.Credential.LogicalIdentifier != this.logicalIdentifier ||
            request.Credential.Provider != CredentialProviderKind.Entra ||
            request.Credential.Kind != CredentialKind.AccessToken)
        {
            throw new ReleaseIdentityMismatchException();
        }
    }

    internal sealed class BrowserTokenAcquirer : IEntraTokenAcquirer
    {
        private readonly InteractiveBrowserCredential selectionCredential;
        private readonly EntraCredentialAccessRequest configuration;
        private readonly Func<InteractiveBrowserCredentialOptions, InteractiveBrowserCredential> credentialFactory;
        private readonly SemaphoreSlim selectionGate = new(1, 1);

        internal BrowserTokenAcquirer(
            InteractiveBrowserCredential selectionCredential,
            EntraCredentialAccessRequest configuration,
            Func<InteractiveBrowserCredentialOptions, InteractiveBrowserCredential>? credentialFactory = null)
        {
            this.selectionCredential = selectionCredential;
            this.configuration = configuration;
            this.credentialFactory = credentialFactory ?? (options => new InteractiveBrowserCredential(options));
        }

        public async ValueTask<EntraAcquiredToken> AcquireAsync(CancellationToken cancellationToken)
        {
            await this.selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var context = new TokenRequestContext(this.configuration.Scopes.Select(scope => scope.Value).ToArray());
                var account = await this.selectionCredential.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(account.HomeAccountId))
                {
                    throw new AuthenticationFailedException("The provider did not identify a stable account.");
                }

                // Selection credentials are mutable. Only a separate, pinned, noninteractive
                // credential may obtain the token whose identity is finalized from this record.
                var credential = this.credentialFactory(CreateBrowserOptions(this.configuration, account));
                var token = await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
                return new EntraAcquiredToken
                {
                    Token = token,
                    AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(
                        CredentialProviderKind.Entra,
                        account.HomeAccountId),
                };
            }
            finally
            {
                this.selectionGate.Release();
            }
        }
    }
}
