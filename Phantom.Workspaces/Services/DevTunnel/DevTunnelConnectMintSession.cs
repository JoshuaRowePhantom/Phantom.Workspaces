using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelConnectMintSession(
    GitHubCredentialAccessExecutor gitHub,
    EntraCredentialAccessExecutor entra,
    DevTunnelConnectManagementApi management) : IDevTunnelConnectMintSession
{
    public ValueTask<CredentialInspection> InspectIdentityAsync(
        DevTunnelIdentityRoute route,
        CancellationToken cancellationToken) => route switch
        {
            GitHubDevTunnelIdentityRoute github => gitHub.InspectAsync(github.Credential, cancellationToken),
            EntraDevTunnelIdentityRoute microsoft => entra.InspectAsync(microsoft.Credential, cancellationToken),
            _ => throw new InvalidOperationException("A private Dev Tunnel requires a supported identity route."),
        };

    public async ValueTask<DevTunnelConnectMintOutcome> MintAsync(
        DevTunnelConnectCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor identitySource,
        ICredentialExecutionContext context,
        ProviderInteractionPermit parentGenerationPermit,
        CancellationToken cancellationToken)
    {
        if (context.PolicyRequest?.Credential is not { Provider: CredentialProviderKind.DevTunnel, Kind: CredentialKind.ConnectToken } ||
            !parentGenerationPermit.BelongsToGeneration(context.RequestIdentifier, context.Revision, context.AcquisitionGeneration) ||
            !request.IdentityRoute.Sources.Any(source => source.Identifier == identitySource.Identifier))
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
        }
        return request.IdentityRoute switch
        {
            GitHubDevTunnelIdentityRoute => await this.MintGitHubAsync(
                request, identitySource, context, parentGenerationPermit, cancellationToken).ConfigureAwait(false),
            EntraDevTunnelIdentityRoute microsoft => await this.MintEntraAsync(
                request, microsoft.Credential, identitySource, context, parentGenerationPermit, cancellationToken).ConfigureAwait(false),
            _ => new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.ProviderFailure },
        };
    }

    private async ValueTask<DevTunnelConnectMintOutcome> MintGitHubAsync(
        DevTunnelConnectCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor source,
        ICredentialExecutionContext context,
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        var used = await gitHub.UsePrivateAsync(new GitHubPrivateCredentialRequest
        {
            Source = source.Kind,
            AccountIdentifier = request.Credential.AccountIdentifier,
        }, context, permit,
            (identity, token) => management.MintAsync(request.Tunnel, DevTunnelIdentityScheme.GitHub, identity, token),
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
        var inspection = await entra.InspectAsync(identityRequest, cancellationToken).ConfigureAwait(false);
        if (entra.FindCache(identityRequest.CreateLogicalIdentifier(), source.Identifier) is not IEntraCredentialSession session)
        {
            return new DevTunnelConnectMintFailed { ErrorCode = AccessErrorCode.InvalidGeneration };
        }
        var metadata = await session.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        if (metadata?.ExpiresOn is not { } expiry || expiry <= DateTimeOffset.UtcNow)
        {
            var acquired = await context.ExecutePrivateProviderDependencyAsync(new EntraAcquisition(
                identityRequest, session, inspection.AcquisitionKeys[source.Identifier], context),
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
        var used = await session.UseGenerationAsync(metadata.Generation, context, permit,
            (identity, token) => management.MintAsync(request.Tunnel, DevTunnelIdentityScheme.Entra, new string(identity.Span), token),
            cancellationToken).ConfigureAwait(false);
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
