using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Auth;

internal sealed class EntraCredentialAccessExecutor :
    ICredentialAccessExecutor<EntraCredentialAccessRequest, EntraCredentialAccessResult>,
    ICredentialCacheAdministration,
    IAsyncDisposable
{
    private readonly Func<EntraCredentialAccessRequest, IEntraCredentialSession> sessionFactory;
    private readonly Dictionary<LogicalCredentialIdentifier, IEntraCredentialSession> sessions = new();
    private readonly object sessionGate = new();
    private readonly Dictionary<CredentialCacheIdentifier, CacheTarget> cacheTargets = new();
    private readonly TimeProvider timeProvider;
    private bool disposed;

    internal EntraCredentialAccessExecutor(
        Func<EntraCredentialAccessRequest, IEntraCredentialSession>? sessionFactory = null,
        TimeProvider? timeProvider = null)
    {
        this.sessionFactory = sessionFactory ?? EntraCredentialSession.Create;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.Entra;

    public async ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        KeyValuePair<LogicalCredentialIdentifier, IEntraCredentialSession>[] owned;
        lock (this.sessionGate)
        {
            this.ThrowIfAdministrationUnavailable();
            owned = this.sessions.ToArray();
        }

        var descriptors = new List<CredentialCacheDescriptor>();
        foreach (var (configuration, session) in owned)
        {
            var generation = await session.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (generation is null)
            {
                continue;
            }

            var identifier = new CredentialCacheIdentifier { Value = generation.Generation.Value };
            var source = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.Entra, configuration.Digest);
            lock (this.sessionGate)
            {
                this.ThrowIfAdministrationUnavailable();
                this.cacheTargets[identifier] = new CacheTarget(session, generation.Generation);
            }

            descriptors.Add(new CredentialCacheDescriptor
            {
                Identifier = identifier,
                Provider = CredentialProviderKind.Entra,
                Kind = CredentialKind.AccessToken,
                Generation = generation.Generation,
                SourceIdentifier = source,
                ExpiresOn = generation.ExpiresOn,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        return descriptors;
    }

    public async ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheTarget target;
        lock (this.sessionGate)
        {
            this.ThrowIfAdministrationUnavailable();
            if (!this.cacheTargets.TryGetValue(identifier, out target!))
            {
                throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
            }
        }

        var outcome = await target.Cache.InvalidateGenerationAsync(target.Generation, cancellationToken).ConfigureAwait(false);
        if (outcome != CredentialGenerationInvalidationOutcome.Invalidated)
        {
            throw new CredentialAdministrationException(outcome == CredentialGenerationInvalidationOutcome.NoLongerCurrent
                ? CredentialAdministrationFailure.NoLongerCurrent
                : CredentialAdministrationFailure.Unavailable);
        }
    }

    internal ICredentialGenerationCache? FindCache(
        LogicalCredentialIdentifier configuration,
        CredentialSourceIdentifier source)
    {
        lock (this.sessionGate)
        {
            var expectedSource = CredentialIdentityFactory.CreateSourceIdentifier(
                CredentialProviderKind.Entra,
                configuration.Digest);
            return !this.disposed && source == expectedSource &&
                this.sessions.TryGetValue(configuration, out var session)
                ? session
                : null;
        }
    }

    public async ValueTask<CredentialInspection> InspectAsync(
        EntraCredentialAccessRequest request,
        CancellationToken cancellationToken)
    {
        var session = this.GetSession(request);
        var cache = await session.InspectCacheAsync(request, cancellationToken).ConfigureAwait(false);
        var source = request.Sources.Single(choice =>
            choice.Identifier == cache.SourceIdentifier && choice.Provider == CredentialProviderKind.Entra);
        var generation = cache.Generation;
        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential with { AccountIdentifier = request.Credential.AccountIdentifier ?? cache.AccountIdentifier },
            Sources = [source with
            {
                Usability = this.IsUsable(generation) &&
                    (request.Credential.AccountIdentifier is null || request.Credential.AccountIdentifier == generation?.AccountIdentifier)
                    ? CredentialSourceUsability.Usable
                    : CredentialSourceUsability.AcquisitionRequired,
            }],
            SelectedSource = source.Identifier,
            AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
            {
                [source.Identifier] = cache.AcquisitionKey with
                {
                    Account = request.Credential.AccountIdentifier ?? cache.AccountIdentifier,
                },
            },
            CachedGenerations = generation is null
                ? new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>()
                : new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection> { [source.Identifier] = generation },
        };
    }

    public async ValueTask<CredentialExecutionOutcome<EntraCredentialAccessResult>> ExecuteAsync(
        EntraCredentialAccessRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        var source = inspection.SelectedSource;
        var policyRequest = context.PolicyRequest;
        if (source is null || source != request.CreateSourceIdentifier() ||
            policyRequest is null || policyRequest.SourceIdentifier != source ||
            policyRequest.Credential.LogicalIdentifier != request.CreateLogicalIdentifier() ||
            authorization.RequestIdentifier != context.RequestIdentifier ||
            authorization.RequestGeneration != policyRequest.RequestGeneration)
        {
            return new CredentialExecutionDenied<EntraCredentialAccessResult>();
        }

        var validation = await context.Policy.ValidateAsync(
            authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
        // A provisional unknown-account authorization permits only the explicitly approved flow.
        if (validation != ReleaseAuthorizationValidation.Valid &&
            !(validation == ReleaseAuthorizationValidation.IdentityMismatch &&
                policyRequest.Credential.AccountIdentifier is null && interactionPermit is not null))
        {
            return new CredentialExecutionDenied<EntraCredentialAccessResult>();
        }

        var session = this.GetSession(request);
        var generation = await session.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        if (!this.IsUsable(generation) ||
            (request.Credential.AccountIdentifier is { } requestedAccount && requestedAccount != generation?.AccountIdentifier) ||
            !await session.IsCurrentGenerationAsync(generation!.Generation, cancellationToken).ConfigureAwait(false))
        {
            if (interactionPermit is null)
            {
                return new CredentialExecutionInteractionRequired<EntraCredentialAccessResult>();
            }

            var outcome = await context.ExecuteProviderAcquisitionAsync(
                new AcquisitionOperation(
                    request with { Credential = policyRequest.Credential },
                    session,
                    inspection.AcquisitionKeys[source],
                    context),
                interactionPermit,
                cancellationToken).ConfigureAwait(false);
            if (outcome is not ProviderAcquisitionSucceeded succeeded)
            {
                return outcome is ProviderAcquisitionDenied
                    ? new CredentialExecutionDenied<EntraCredentialAccessResult>()
                    : new CredentialExecutionFailed<EntraCredentialAccessResult>
                    {
                        ErrorCode = outcome is ProviderAcquisitionFailed failed ? failed.ErrorCode : AccessErrorCode.ProviderFailure,
                        CanRetry = true,
                    };
            }

            generation = await session.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (generation?.Generation != succeeded.CommittedGeneration)
            {
                return new CredentialExecutionInteractionRequired<EntraCredentialAccessResult>();
            }
        }

        if (generation?.AccountIdentifier is not { } account ||
            !this.IsUsable(generation) ||
            (policyRequest.Credential.AccountIdentifier is { } expected && expected != account))
        {
            return new CredentialExecutionDenied<EntraCredentialAccessResult>();
        }

        var material = await session.CopyGenerationAsync(generation.Generation, cancellationToken).ConfigureAwait(false);
        if (material is null)
        {
            return new CredentialExecutionInteractionRequired<EntraCredentialAccessResult>();
        }

        return new CredentialExecutionReady<EntraCredentialAccessResult>
        {
            ResolvedIdentity = new()
            {
                LogicalIdentifier = request.CreateLogicalIdentifier(),
                SourceIdentifier = source,
                AccountIdentifier = account,
                RequestIdentifier = policyRequest.RequestIdentifier,
                RequestGeneration = policyRequest.RequestGeneration,
            },
            Materializer = new Materializer(material, authorization, context, session, this.timeProvider),
        };
    }

    public async ValueTask DisposeAsync()
    {
        IEntraCredentialSession[] owned;
        lock (this.sessionGate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            owned = this.sessions.Values.ToArray();
            this.sessions.Clear();
            this.cacheTargets.Clear();
        }

        foreach (var session in owned)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool IsUsable(CredentialGenerationInspection? generation) =>
        generation?.AccountIdentifier is not null &&
        generation.ExpiresOn is { } expiresOn && this.timeProvider.GetUtcNow() < expiresOn;

    private void ThrowIfAdministrationUnavailable()
    {
        if (this.disposed)
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        }
    }

    internal IEntraCredentialSession GetSession(EntraCredentialAccessRequest request)
    {
        lock (this.sessionGate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            var identifier = request.CreateLogicalIdentifier();
            if (!this.sessions.TryGetValue(identifier, out var session))
            {
                session = this.sessionFactory(request);
                this.sessions.Add(identifier, session);
            }

            return session;
        }
    }

    private sealed record CacheTarget(
        ICredentialGenerationCache Cache,
        CredentialMaterialGenerationIdentifier Generation);

    private sealed class AcquisitionOperation(
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

    private sealed class Materializer(
        EntraCachedMaterial material,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        IEntraCredentialSession session,
        TimeProvider timeProvider) : IAccessResultMaterializer<EntraCredentialAccessResult>
    {
        public ValueTask<EntraCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = context.PolicyRequest!;
            if (request.Credential.AccountIdentifier != material.Inspection.AccountIdentifier)
            {
                throw new ReleaseIdentityMismatchException();
            }

            return ValueTask.FromResult(new EntraCredentialAccessResult
            {
                ExpiresOn = material.Inspection.ExpiresOn!.Value,
                AccessToken = new SensitiveValueLease(
                    material.Token,
                    new CredentialLeaseBinding
                    {
                        AuthorizationIdentifier = authorization.Identifier,
                        Request = request,
                        MaterialGeneration = material.Inspection.Generation,
                        ExpiresOn = material.Inspection.ExpiresOn,
                    },
                    context.CreateLeaseContext(session),
                    timeProvider),
            });
        }

        public ValueTask DisposeAsync()
        {
            material.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
