using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelIdentityCredentialAccessExecutor(
    ICredentialAccessExecutor<GitHubCredentialAccessRequest, GitHubCredentialAccessResult> gitHub,
    ICredentialAccessExecutor<EntraCredentialAccessRequest, EntraCredentialAccessResult> entra) :
    ICredentialAccessExecutor<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>
{
    public CredentialExecutorKind Kind => CredentialExecutorKind.DevTunnelIdentity;

    public async ValueTask<CredentialInspection> InspectAsync(
        DevTunnelIdentityCredentialAccessRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCanonical(request))
        {
            return new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = [],
            };
        }

        CredentialInspection? dependency = request.Route switch
        {
            GitHubDevTunnelIdentityRoute route => await gitHub.InspectAsync(
                route.Credential, cancellationToken).ConfigureAwait(false),
            EntraDevTunnelIdentityRoute route => await entra.InspectAsync(
                route.Credential, cancellationToken).ConfigureAwait(false),
            AnonymousDevTunnelIdentityRoute => null,
            _ => throw new InvalidOperationException("Unsupported Dev Tunnel identity route."),
        };
        var sources = dependency is null
            ? new[] { DevTunnelIdentityCredentialAccessRequest.CreateSource(request.Route, null) }
            : dependency.Sources.Select(source => DevTunnelIdentityCredentialAccessRequest.CreateSource(request.Route, source)).ToArray();
        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential with
            {
                AccountIdentifier = dependency?.Credential.AccountIdentifier ?? request.Credential.AccountIdentifier,
            },
            Sources = sources.Where(source => request.Sources.Any(allowed => allowed.Identifier == source.Identifier)).ToArray(),
            AcquisitionKeys = sources.ToDictionary(source => source.Identifier, source => new ProviderAcquisitionKey
            {
                Configuration = request.Credential.LogicalIdentifier,
                Source = source.Identifier,
                Account = dependency?.Credential.AccountIdentifier ?? request.Credential.AccountIdentifier,
                Protocol = ProviderProtocolKind.TunnelIdentity,
            }),
        };
    }

    public async ValueTask<CredentialExecutionOutcome<DevTunnelIdentityCredentialAccessResult>> ExecuteAsync(
        DevTunnelIdentityCredentialAccessRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!IsCanonical(request) || context.PolicyRequest is not { } policyRequest ||
            policyRequest.Credential.LogicalIdentifier != request.Credential.LogicalIdentifier ||
            authorization.RequestIdentifier != policyRequest.RequestIdentifier ||
            authorization.RequestGeneration != policyRequest.RequestGeneration ||
            inspection.SelectedSource != policyRequest.SourceIdentifier ||
            !inspection.Sources.Any(source => source.Identifier == policyRequest.SourceIdentifier))
        {
            return new CredentialExecutionDenied<DevTunnelIdentityCredentialAccessResult>();
        }

        var validation = await context.Policy.ValidateAsync(
            authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
        if (validation != ReleaseAuthorizationValidation.Valid &&
            !(validation == ReleaseAuthorizationValidation.IdentityMismatch && policyRequest.Credential.AccountIdentifier is null))
        {
            return new CredentialExecutionDenied<DevTunnelIdentityCredentialAccessResult>();
        }

        return request.Route switch
        {
            GitHubDevTunnelIdentityRoute route => await AcquireDependencyAsync(
                request, route.Credential, result => result.Lease, authorization, context, cancellationToken).ConfigureAwait(false),
            EntraDevTunnelIdentityRoute route => await AcquireDependencyAsync(
                request, route.Credential, result => result.AccessToken, authorization, context, cancellationToken).ConfigureAwait(false),
            AnonymousDevTunnelIdentityRoute => new CredentialExecutionReady<DevTunnelIdentityCredentialAccessResult>
            {
                Materializer = new AnonymousMaterializer(),
            },
            _ => throw new InvalidOperationException("Unsupported Dev Tunnel identity route."),
        };
    }

    private static bool IsCanonical(DevTunnelIdentityCredentialAccessRequest request) =>
        request.Credential.Provider == CredentialProviderKind.DevTunnel &&
        request.Credential.Kind == (request.Route.Scheme == DevTunnelIdentityScheme.Anonymous
            ? CredentialKind.Anonymous : CredentialKind.IdentityToken) &&
        request.Credential.LogicalIdentifier == DevTunnelIdentityCredentialAccessRequest.CreateLogicalIdentifier(request.Route) &&
        (request.Route is not AnonymousDevTunnelIdentityRoute || request.Credential.AccountIdentifier is null);

    private static async ValueTask<CredentialExecutionOutcome<DevTunnelIdentityCredentialAccessResult>> AcquireDependencyAsync<TResult>(
        DevTunnelIdentityCredentialAccessRequest request,
        CredentialAccessRequest<TResult> dependency,
        Func<TResult, SensitiveValueLease> selectLease,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
        where TResult : ICredentialAccessResult
    {
        var policyRequest = context.PolicyRequest!;
        var source = dependency.Sources.SingleOrDefault(candidate =>
            DevTunnelIdentityCredentialAccessRequest.CreateSource(request.Route, candidate).Identifier == policyRequest.SourceIdentifier);
        if (source is null)
        {
            return new CredentialExecutionMissing<DevTunnelIdentityCredentialAccessResult>();
        }

        var outcome = await context.AcquireDependencyAsync(dependency with
        {
            Credential = dependency.Credential with
            {
                AccountIdentifier = policyRequest.Credential.AccountIdentifier ?? dependency.Credential.AccountIdentifier,
            },
            Sources = [source],
        }, cancellationToken).ConfigureAwait(false);
        if (outcome is not AccessSucceeded<TResult> success)
        {
            return outcome switch
            {
                AccessDenied<TResult> or AccessCancelled<TResult> => new CredentialExecutionDenied<DevTunnelIdentityCredentialAccessResult>(),
                AccessFailed<TResult> failure => new CredentialExecutionFailed<DevTunnelIdentityCredentialAccessResult>
                {
                    ErrorCode = failure.ErrorCode,
                    CanRetry = failure.CanRetry,
                },
                AccessShuttingDown<TResult> => new CredentialExecutionFailed<DevTunnelIdentityCredentialAccessResult>
                {
                    ErrorCode = AccessErrorCode.Disposed,
                    CanRetry = false,
                },
                _ => new CredentialExecutionInteractionRequired<DevTunnelIdentityCredentialAccessResult>(),
            };
        }

        var ownsResult = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = selectLease(success.Result);
            if (lease.AccountIdentifier is not { } account ||
                (policyRequest.Credential.AccountIdentifier is { } expected && expected != account))
            {
                return new CredentialExecutionFailed<DevTunnelIdentityCredentialAccessResult>
                {
                    ErrorCode = AccessErrorCode.IdentityMismatch,
                    CanRetry = false,
                };
            }

            var ready = new CredentialExecutionReady<DevTunnelIdentityCredentialAccessResult>
            {
                ResolvedIdentity = new ResolvedCredentialIdentity
                {
                    LogicalIdentifier = policyRequest.Credential.LogicalIdentifier,
                    SourceIdentifier = policyRequest.SourceIdentifier,
                    AccountIdentifier = account,
                    RequestIdentifier = policyRequest.RequestIdentifier,
                    RequestGeneration = policyRequest.RequestGeneration,
                },
                Materializer = new Materializer(lease, success.Result, request.Route.Scheme, authorization, context),
            };
            ownsResult = false;
            return ready;
        }
        finally
        {
            if (ownsResult)
            {
                await success.Result.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class AnonymousMaterializer : IAccessResultMaterializer<DevTunnelIdentityCredentialAccessResult>
    {
        public ValueTask<DevTunnelIdentityCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DevTunnelIdentityCredentialAccessResult(null, DevTunnelIdentityScheme.Anonymous));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Materializer(
        SensitiveValueLease lease,
        IAsyncDisposable owner,
        DevTunnelIdentityScheme scheme,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context) : IAccessResultMaterializer<DevTunnelIdentityCredentialAccessResult>
    {
        private readonly object gate = new();
        private IAsyncDisposable? owner = owner;

        public ValueTask<DevTunnelIdentityCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.owner is null, this);
                var view = SensitiveValueLease.CreateAuthorizedView(lease, this.owner, authorization.Identifier, context);
                var result = new DevTunnelIdentityCredentialAccessResult(view, scheme);
                this.owner = null;
                return ValueTask.FromResult(result);
            }
        }

        public async ValueTask DisposeAsync()
        {
            IAsyncDisposable? result;
            lock (this.gate)
            {
                result = this.owner;
                this.owner = null;
            }
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
