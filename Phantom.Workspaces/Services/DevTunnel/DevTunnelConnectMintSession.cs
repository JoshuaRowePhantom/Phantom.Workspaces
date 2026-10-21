using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelConnectMintSession(
    GitHubCredentialAccessExecutor gitHub,
    EntraCredentialAccessExecutor entra,
    OAuthCredentialAccessExecutor oauth,
    DevTunnelConnectManagementApi management) : IDevTunnelConnectMintSession
{
    public ValueTask<CredentialInspection> InspectIdentityAsync(
        DevTunnelIdentityRoute route,
        CancellationToken cancellationToken) => route switch
        {
            GitHubDevTunnelIdentityRoute github => gitHub.InspectAsync(github.Credential, cancellationToken),
            EntraDevTunnelIdentityRoute microsoft => entra.InspectAsync(microsoft.Credential, cancellationToken),
            OAuthDevTunnelIdentityRoute configured => oauth.InspectAsync(configured.Credential, cancellationToken),
            _ => throw new InvalidOperationException("A private Dev Tunnel requires a supported identity route."),
        };

    public async ValueTask<DevTunnelConnectMintOutcome> MintAsync(
        DevTunnelConnectCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor identitySource,
        ICredentialExecutionContext context,
        ProviderAcquisitionKey parentAcquisitionKey,
        ProviderInteractionPermit parentGenerationPermit,
        CancellationToken cancellationToken)
    {
        if (parentAcquisitionKey is null || parentGenerationPermit is null ||
            context.PolicyRequest is not { Credential: { Provider: CredentialProviderKind.DevTunnel, Kind: CredentialKind.ConnectToken } } policy ||
            policy.RequestIdentifier != context.RequestIdentifier ||
            policy.RequestGeneration != context.Revision ||
            policy.RequestorIdentifier != context.Requestor.Identifier ||
            policy.RequestorIdentifier != request.Requestor.Identifier ||
            !SameUse(policy.Use, context.Use) || !SameUse(policy.Use, request.Use) ||
            parentAcquisitionKey.Protocol != ProviderProtocolKind.TunnelConnect ||
            parentAcquisitionKey.Configuration != policy.Credential.LogicalIdentifier ||
            parentAcquisitionKey.Configuration != DevTunnelConnectCredentialAccessRequest.CreateLogicalIdentifier(request.Tunnel, request.IdentityRoute) ||
            parentAcquisitionKey.Source != policy.SourceIdentifier ||
            parentAcquisitionKey.Account != policy.Credential.AccountIdentifier ||
            parentAcquisitionKey.Source != DevTunnelConnectCredentialAccessRequest.CreateSource(identitySource).Identifier ||
            !parentGenerationPermit.Authorizes(context.RequestIdentifier, context.Revision,
                CredentialExecutorKind.DevTunnelConnect, context.AcquisitionGeneration, parentAcquisitionKey) ||
            !parentGenerationPermit.AuthorizesProvider(CredentialExecutorKind.DevTunnelConnect, parentAcquisitionKey) ||
            !request.IdentityRoute.Sources.Any(source => source.Identifier == identitySource.Identifier))
        {
            return new DevTunnelConnectMintDenied();
        }
        return request.IdentityRoute switch
        {
            GitHubDevTunnelIdentityRoute => await this.MintGitHubAsync(
                request, identitySource, context, parentGenerationPermit, cancellationToken).ConfigureAwait(false),
            EntraDevTunnelIdentityRoute microsoft => await this.MintEntraAsync(
                request, microsoft.Credential, identitySource, context, parentGenerationPermit, cancellationToken).ConfigureAwait(false),
            OAuthDevTunnelIdentityRoute configured => await this.MintOAuthAsync(
                request, configured.Credential, context, parentAcquisitionKey, parentGenerationPermit, cancellationToken).ConfigureAwait(false),
            _ => new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.ProviderFailure },
        };
    }

    private static bool SameUse(CredentialUseDescriptor left, CredentialUseDescriptor right) =>
        (left with { MemoryScopes = right.MemoryScopes }) == right &&
        left.MemoryScopes.SequenceEqual(right.MemoryScopes);

    private async ValueTask<DevTunnelConnectMintOutcome> MintGitHubAsync(
        DevTunnelConnectCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor source,
        ICredentialExecutionContext context,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        if (source.Provider != CredentialProviderKind.GitHub ||
            source.Kind is not (CredentialSourceKind.Environment or CredentialSourceKind.GitHubCli) ||
            source.Identifier != GitHubCredentialAccessRequest.CreateSource(source.Kind).Identifier)
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
        }
        using var mintedMaterial = new MintedMaterialOwner();
        var used = await gitHub.UsePrivateAsync(new GitHubPrivateCredentialRequest
        {
            Source = source.Kind,
            AccountIdentifier = request.Credential.AccountIdentifier,
        }, context, permit,
            (identity, token) => mintedMaterial.CaptureAsync(
                management.MintAsync(request.Tunnel, DevTunnelIdentityScheme.GitHub, identity, token)),
            cancellationToken).ConfigureAwait(false);
        if (used is not GitHubPrivateCredentialUseSucceeded<DevTunnelConnectManagementOutcome> success)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new DevTunnelConnectMintFailed
            {
                ErrorCode = used is GitHubPrivateCredentialUseFailed<DevTunnelConnectManagementOutcome> failed
                    ? failed.ErrorCode : AccessErrorCode.ProviderFailure,
            };
        }
        if (success.Result is DevTunnelConnectManagementUnauthorized)
        {
            await gitHub.GetGenerationCache(source.Identifier)
                .InvalidateGenerationAsync(success.Generation, cancellationToken).ConfigureAwait(false);
        }
        return Complete(success.Result, success.AccountIdentifier);
    }

    private async ValueTask<DevTunnelConnectMintOutcome> MintEntraAsync(
        DevTunnelConnectCredentialAccessRequest request,
        EntraCredentialAccessRequest identityRequest,
        CredentialSourceChoiceDescriptor source,
        ICredentialExecutionContext context,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        identityRequest = identityRequest with
        {
            Credential = identityRequest.Credential with
            {
                AccountIdentifier = request.Credential.AccountIdentifier ?? identityRequest.Credential.AccountIdentifier,
            },
            Sources = [source],
        };
        if (source.Provider != CredentialProviderKind.Entra ||
            source.Identifier != identityRequest.CreateSourceIdentifier())
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
        }
        var session = entra.GetSession(identityRequest);
        var inspection = await session.InspectCacheAsync(identityRequest, cancellationToken).ConfigureAwait(false);
        identityRequest = identityRequest with
        {
            Credential = identityRequest.Credential with
            {
                AccountIdentifier = identityRequest.Credential.AccountIdentifier ?? inspection.AccountIdentifier,
            },
        };
        var metadata = inspection.Generation;
        if (metadata?.AccountIdentifier is null || metadata.ExpiresOn <= DateTimeOffset.UtcNow)
        {
            var acquired = await context.ExecutePrivateProviderDependencyAsync(new EntraAcquisition(
                identityRequest, session, inspection.AcquisitionKey with
                {
                    Account = identityRequest.Credential.AccountIdentifier,
                }, context),
                permit, cancellationToken).ConfigureAwait(false);
            if (acquired is not ProviderAcquisitionSucceeded receipt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new DevTunnelConnectMintFailed
                {
                    ErrorCode = acquired is ProviderAcquisitionFailed failed ? failed.ErrorCode : AccessErrorCode.ProviderFailure,
                };
            }
            metadata = await session.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (metadata?.Generation != receipt.CommittedGeneration)
            {
                return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
            }
        }
        if (metadata.AccountIdentifier is not { } account ||
            (identityRequest.Credential.AccountIdentifier is { } expected && expected != account) ||
            !permit.BelongsToGeneration(context.RequestIdentifier, context.Revision, context.AcquisitionGeneration))
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
        }
        using var mintedMaterial = new MintedMaterialOwner();
        var used = await session.UseGenerationAsync(metadata.Generation, context, permit,
            (identity, token) => mintedMaterial.CaptureAsync(
                management.MintAsync(request.Tunnel, DevTunnelIdentityScheme.Entra, new string(identity.Span), token)),
            cancellationToken).ConfigureAwait(false);
        if (used is EntraGenerationUseDenied<DevTunnelConnectManagementOutcome>)
        {
            return new DevTunnelConnectMintDenied();
        }
        if (used is not EntraGenerationUseSucceeded<DevTunnelConnectManagementOutcome> success)
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
        }
        if (success.Result is DevTunnelConnectManagementUnauthorized)
        {
            await session.InvalidateGenerationAsync(metadata.Generation, cancellationToken).ConfigureAwait(false);
        }
        return Complete(success.Result, account);
    }

    private async ValueTask<DevTunnelConnectMintOutcome> MintOAuthAsync(
        DevTunnelConnectCredentialAccessRequest request,
        OAuthCredentialAccessRequest identityRequest,
        ICredentialExecutionContext context,
        ProviderAcquisitionKey parentAcquisitionKey,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        identityRequest = identityRequest with
        {
            Credential = identityRequest.Credential with
            {
                AccountIdentifier = request.Credential.AccountIdentifier ?? identityRequest.Credential.AccountIdentifier,
            },
        };
        IOAuthPrivateCredentialProvider provider = oauth;
        var metadata = await provider.InspectGenerationAsync(identityRequest, cancellationToken).ConfigureAwait(false);
        if (metadata?.AccountIdentifier is null || metadata.ExpiresOn <= DateTimeOffset.UtcNow)
        {
            var acquired = await provider.AcquirePrivateAsync(identityRequest, context, parentAcquisitionKey, permit, cancellationToken).ConfigureAwait(false);
            if (acquired is ProviderAcquisitionDenied)
            {
                return new DevTunnelConnectMintDenied();
            }
            if (acquired is not ProviderAcquisitionSucceeded receipt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new DevTunnelConnectMintFailed
                {
                    ErrorCode = acquired is ProviderAcquisitionFailed failed ? failed.ErrorCode : AccessErrorCode.ProviderFailure,
                };
            }
            metadata = await provider.InspectGenerationAsync(identityRequest, cancellationToken).ConfigureAwait(false);
            if (metadata?.Generation != receipt.CommittedGeneration)
            {
                return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
            }
        }
        if (metadata.AccountIdentifier is not { } account ||
            (identityRequest.Credential.AccountIdentifier is { } expected && expected != account))
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
        }
        using var mintedMaterial = new MintedMaterialOwner();
        var used = await provider.UseGenerationAsync(identityRequest, metadata.Generation, account, context, parentAcquisitionKey, permit,
            (identity, token) => mintedMaterial.CaptureAsync(
                management.MintAsync(request.Tunnel, DevTunnelIdentityScheme.OAuth, identity, token)),
            cancellationToken).ConfigureAwait(false);
        if (used is OAuthGenerationUseDenied<DevTunnelConnectManagementOutcome>)
        {
            return new DevTunnelConnectMintDenied();
        }
        if (used is not OAuthGenerationUseSucceeded<DevTunnelConnectManagementOutcome> success)
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
        }
        if (success.Result is DevTunnelConnectManagementUnauthorized)
        {
            await provider.InvalidateGenerationAsync(identityRequest, metadata.Generation, cancellationToken).ConfigureAwait(false);
        }
        return Complete(success.Result, account);
    }

    private static DevTunnelConnectMintOutcome Complete(
        DevTunnelConnectManagementOutcome outcome,
        CredentialAccountIdentifier account)
    {
        if (outcome is DevTunnelConnectManagementSucceeded success)
        {
            using (success)
            {
                return new DevTunnelConnectMintSucceeded
                {
                    Material = success.Material.ToArray(),
                    Account = account,
                    Endpoint = success.Endpoint,
                    ExpiresOn = success.ExpiresOn,
                };
            }
        }
        return outcome is DevTunnelConnectManagementUnauthorized
            ? new DevTunnelConnectMintUnauthorized()
            : new DevTunnelConnectMintFailed
            {
                ErrorCode = outcome is DevTunnelConnectManagementFailed failed ? failed.ErrorCode : AccessErrorCode.ProviderFailure,
            };
    }

    // Provider executors are borrowed from the broker registry, which owns their lifetimes.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class MintedMaterialOwner : IDisposable
    {
        private DevTunnelConnectManagementOutcome? outcome;

        public async ValueTask<DevTunnelConnectManagementOutcome> CaptureAsync(
            ValueTask<DevTunnelConnectManagementOutcome> pending)
        {
            this.outcome = await pending.ConfigureAwait(false);
            return this.outcome;
        }

        public void Dispose() => (this.outcome as IDisposable)?.Dispose();
    }

    private sealed class EntraAcquisition(
        EntraCredentialAccessRequest request,
        IEntraCredentialSession session,
        ProviderAcquisitionKey key,
        ICredentialExecutionContext context) : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.Entra;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;

        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => session.ExecuteAsync(request, commitContext, cancellationToken);
    }
}
