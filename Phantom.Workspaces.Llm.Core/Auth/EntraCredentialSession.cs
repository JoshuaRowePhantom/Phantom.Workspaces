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
}

internal sealed class EntraCredentialSession : IEntraCredentialSession
{
    private readonly EntraPinnedTokenProvider provider;
    private readonly IEntraTokenAcquirer acquirer;
    private readonly LogicalCredentialIdentifier logicalIdentifier;
    private readonly CredentialSourceIdentifier sourceIdentifier;
    private readonly TimeProvider timeProvider;
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
        if (!EntraInteractiveCredentialFactory.TryParseAuthority(
            configuration.Authority.Value.AbsoluteUri, out var authorityHost, out var tenantIdentifier))
        {
            throw new ArgumentException("An absolute Entra authority is required.");
        }

        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            AuthorityHost = authorityHost,
            TenantId = tenantIdentifier,
            ClientId = configuration.Client.Value,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "phantom-access-entra:" + configuration.CreateLogicalIdentifier().Digest,
            },
        });
        return new EntraCredentialSession(
            configuration,
            credential,
            new BrowserTokenAcquirer(credential, configuration.Scopes.Select(scope => scope.Value).ToArray()));
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

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref this.disposed, 1);
        this.provider.DisposeBrokerCache();
        return ValueTask.CompletedTask;
    }

    private ProviderAcquisitionKey CreateAcquisitionKey(EntraCredentialAccessRequest request) => new()
    {
        Configuration = this.logicalIdentifier,
        Source = this.sourceIdentifier,
        Account = request.Credential.AccountIdentifier,
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

    private sealed class BrowserTokenAcquirer(
        InteractiveBrowserCredential credential,
        string[] scopes) : IEntraTokenAcquirer
    {
        public async ValueTask<EntraAcquiredToken> AcquireAsync(CancellationToken cancellationToken)
        {
            var context = new TokenRequestContext(scopes);
            var account = await credential.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
            var token = await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            return new EntraAcquiredToken
            {
                Token = token,
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(
                    CredentialProviderKind.Entra,
                    account.HomeAccountId),
            };
        }
    }
}
