using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.Services.DevTunnel;

internal abstract record DevTunnelConnectMintOutcome;
internal sealed record DevTunnelConnectMintDenied : DevTunnelConnectMintOutcome;
internal sealed record DevTunnelConnectMintUnauthorized : DevTunnelConnectMintOutcome;
internal sealed record DevTunnelConnectMintFailed : DevTunnelConnectMintOutcome
{
    public required AccessErrorCode ErrorCode { get; init; }
}
internal sealed record DevTunnelConnectMintSucceeded : DevTunnelConnectMintOutcome, IDisposable
{
    public required char[] Material { get; init; }
    public required CredentialAccountIdentifier Account { get; init; }
    public required DateTimeOffset ExpiresOn { get; init; }
    public required Uri Endpoint { get; init; }

    public void Dispose() => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.Material.AsSpan()));
}

internal interface IDevTunnelConnectMintSession : IAsyncDisposable
{
    ValueTask<CredentialInspection> InspectIdentityAsync(
        DevTunnelIdentityRoute route,
        CancellationToken cancellationToken);

    ValueTask<DevTunnelConnectMintOutcome> MintAsync(
        DevTunnelConnectCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor identitySource,
        ICredentialExecutionContext context,
        ProviderAcquisitionKey parentAcquisitionKey,
        ProviderInteractionPermit parentGenerationPermit,
        CancellationToken cancellationToken);
}

internal sealed class DevTunnelConnectCredentialAccessExecutor :
    ICredentialAccessExecutor<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>,
    ICredentialCacheAdministration,
    IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<CacheKey, GenerationCache> caches = [];
    private readonly Dictionary<CredentialCacheIdentifier, (GenerationCache Cache, CredentialMaterialGenerationIdentifier Generation)> administrationEntries = [];
    private readonly HashSet<UserAuthorizationRequestIdentifier> awaitingRenewal = [];
    private readonly IDevTunnelConnectMintSession session;
    private readonly IUserAuthorizationRegistry registry;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    internal DevTunnelConnectCredentialAccessExecutor(
        IDevTunnelConnectMintSession session,
        IUserAuthorizationRegistry registry,
        TimeProvider? timeProvider = null)
    {
        this.session = session;
        this.registry = registry;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.registry.Changed += this.RegistryChanged;
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.DevTunnelConnect;

    public async ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        KeyValuePair<CacheKey, GenerationCache>[] entries;
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            entries = this.caches.ToArray();
        }
        var result = new List<CredentialCacheDescriptor>();
        foreach (var entry in entries)
        {
            var metadata = await entry.Value.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                continue;
            }
            var identifier = new CredentialCacheIdentifier { Value = metadata.Generation.Value };
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.administrationEntries[identifier] = (entry.Value, metadata.Generation);
            }
            result.Add(new CredentialCacheDescriptor
            {
                Identifier = identifier,
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                Generation = metadata.Generation,
                SourceIdentifier = entry.Key.Source,
                ExpiresOn = metadata.ExpiresOn,
            });
        }
        return result;
    }

    public async ValueTask DeleteAsync(CredentialCacheIdentifier identifier, CancellationToken cancellationToken = default)
    {
        (GenerationCache Cache, CredentialMaterialGenerationIdentifier Generation) entry;
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.administrationEntries.TryGetValue(identifier, out entry))
            {
                throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
            }
        }
        var outcome = await entry.Cache.InvalidateGenerationAsync(entry.Generation, cancellationToken).ConfigureAwait(false);
        if (outcome != CredentialGenerationInvalidationOutcome.Invalidated)
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.NoLongerCurrent);
        }
    }

    public async ValueTask<CredentialInspection> InspectAsync(
        DevTunnelConnectCredentialAccessRequest request,
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
        if (request.Tunnel.Access == DevTunnelAccessKind.Anonymous)
        {
            return new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = [DevTunnelConnectCredentialAccessRequest.CreateSource(null)],
            };
        }

        var identity = await this.session.InspectIdentityAsync(request.IdentityRoute, cancellationToken).ConfigureAwait(false);
        var sources = new List<CredentialSourceChoiceDescriptor>();
        var generations = new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>();
        foreach (var identitySource in identity.Sources)
        {
            var source = DevTunnelConnectCredentialAccessRequest.CreateSource(identitySource);
            if (!request.Sources.Any(allowed => allowed.Identifier == source.Identifier))
            {
                continue;
            }
            var cache = this.GetCache(request.Credential.LogicalIdentifier, source.Identifier);
            var generation = await cache.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            sources.Add(source with
            {
                Usability = this.IsUsable(generation, request.Credential.AccountIdentifier)
                    ? CredentialSourceUsability.Usable
                    : identitySource.Usability == CredentialSourceUsability.Missing
                        ? CredentialSourceUsability.Missing : CredentialSourceUsability.AcquisitionRequired,
            });
            if (generation is not null)
            {
                generations.Add(source.Identifier, generation);
            }
        }
        var account = request.Credential.AccountIdentifier ?? identity.Credential.AccountIdentifier;
        if (account is null && sources.Count == 1 && generations.TryGetValue(sources[0].Identifier, out var cached))
        {
            account = cached.AccountIdentifier;
        }
        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential with { AccountIdentifier = account },
            Sources = sources,
            CachedGenerations = generations,
            AcquisitionKeys = sources.ToDictionary(source => source.Identifier, source => new ProviderAcquisitionKey
            {
                Configuration = request.Credential.LogicalIdentifier,
                Source = source.Identifier,
                Account = account,
                Protocol = ProviderProtocolKind.TunnelConnect,
            }),
        };
    }

    public async ValueTask<CredentialExecutionOutcome<DevTunnelConnectCredentialAccessResult>> ExecuteAsync(
        DevTunnelConnectCredentialAccessRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!IsCanonical(request) || context.PolicyRequest is not { } policyRequest ||
            authorization.RequestIdentifier != policyRequest.RequestIdentifier ||
            authorization.RequestGeneration != policyRequest.RequestGeneration ||
            inspection.SelectedSource != policyRequest.SourceIdentifier ||
            policyRequest.Credential.LogicalIdentifier != request.Credential.LogicalIdentifier ||
            !inspection.Sources.Any(source => source.Identifier == policyRequest.SourceIdentifier))
        {
            return new CredentialExecutionDenied<DevTunnelConnectCredentialAccessResult>();
        }
        var validation = await context.Policy.ValidateAsync(
            authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
        if (validation != ReleaseAuthorizationValidation.Valid &&
            !(validation == ReleaseAuthorizationValidation.IdentityMismatch &&
                policyRequest.Credential.AccountIdentifier is null && interactionPermit is not null))
        {
            return new CredentialExecutionDenied<DevTunnelConnectCredentialAccessResult>();
        }
        if (request.Tunnel.Access == DevTunnelAccessKind.Anonymous)
        {
            return new CredentialExecutionReady<DevTunnelConnectCredentialAccessResult>
            {
                Materializer = new AnonymousMaterializer(request.Tunnel.Endpoint!),
            };
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        var cache = this.GetCache(request.Credential.LogicalIdentifier, policyRequest.SourceIdentifier);
        var metadata = await cache.InspectGenerationAsync(cancellation.Token).ConfigureAwait(false);
        CredentialMaterialGenerationIdentifier generation;
        if (!this.IsUsable(metadata, policyRequest.Credential.AccountIdentifier))
        {
            if (interactionPermit is null)
            {
                return new CredentialExecutionInteractionRequired<DevTunnelConnectCredentialAccessResult>();
            }
            var identitySource = request.IdentityRoute.Sources.SingleOrDefault(source =>
                DevTunnelConnectCredentialAccessRequest.CreateSource(source).Identifier == policyRequest.SourceIdentifier);
            if (identitySource is null)
            {
                return new CredentialExecutionMissing<DevTunnelConnectCredentialAccessResult>();
            }
            var outcome = await context.ExecuteProviderAcquisitionAsync(new Acquisition(
                this, request with { Credential = policyRequest.Credential }, identitySource, cache,
                inspection.AcquisitionKeys[policyRequest.SourceIdentifier], context),
                interactionPermit, cancellation.Token).ConfigureAwait(false);
            if (outcome is ProviderAcquisitionRenewalRequired)
            {
                lock (this.gate)
                {
                    return this.awaitingRenewal.Add(context.RequestIdentifier)
                        ? new CredentialExecutionRenewalRequired<DevTunnelConnectCredentialAccessResult>()
                        : new CredentialExecutionFailed<DevTunnelConnectCredentialAccessResult>
                        {
                            ErrorCode = AccessErrorCode.ProviderFailure,
                            CanRetry = false,
                        };
                }
            }
            if (outcome is not ProviderAcquisitionSucceeded succeeded)
            {
                return outcome is ProviderAcquisitionDenied
                    ? new CredentialExecutionDenied<DevTunnelConnectCredentialAccessResult>()
                    : new CredentialExecutionFailed<DevTunnelConnectCredentialAccessResult>
                    {
                        ErrorCode = outcome is ProviderAcquisitionFailed failure ? failure.ErrorCode : AccessErrorCode.ProviderFailure,
                        CanRetry = true,
                    };
            }
            generation = succeeded.CommittedGeneration;
        }
        else
        {
            generation = metadata!.Generation;
        }

        var snapshot = cache.Copy(generation);
        if (snapshot is null)
        {
            return new CredentialExecutionInteractionRequired<DevTunnelConnectCredentialAccessResult>();
        }
        if (!this.IsUsable(snapshot.Inspection, policyRequest.Credential.AccountIdentifier))
        {
            snapshot.Dispose();
            return new CredentialExecutionInteractionRequired<DevTunnelConnectCredentialAccessResult>();
        }
        return new CredentialExecutionReady<DevTunnelConnectCredentialAccessResult>
        {
            ResolvedIdentity = new ResolvedCredentialIdentity
            {
                LogicalIdentifier = policyRequest.Credential.LogicalIdentifier,
                SourceIdentifier = policyRequest.SourceIdentifier,
                AccountIdentifier = snapshot.Inspection.AccountIdentifier!,
                RequestIdentifier = policyRequest.RequestIdentifier,
                RequestGeneration = policyRequest.RequestGeneration,
            },
            Materializer = new Materializer(snapshot, authorization, context, cache, this.timeProvider),
        };
    }

    private bool IsUsable(
        CredentialGenerationInspection? generation,
        CredentialAccountIdentifier? account) =>
        generation?.AccountIdentifier is not null &&
        generation.ExpiresOn > this.timeProvider.GetUtcNow() &&
        (account is null || account == generation.AccountIdentifier);

    private static bool IsCanonical(DevTunnelConnectCredentialAccessRequest request) =>
        request.Credential.Provider == CredentialProviderKind.DevTunnel &&
        request.Credential.Kind == (request.Tunnel.Access == DevTunnelAccessKind.Anonymous
            ? CredentialKind.Anonymous : CredentialKind.ConnectToken) &&
        request.Credential.LogicalIdentifier ==
            DevTunnelConnectCredentialAccessRequest.CreateLogicalIdentifier(request.Tunnel, request.IdentityRoute) &&
        (request.Tunnel.Endpoint is null || IsRelayEndpoint(request.Tunnel.Endpoint)) &&
        (request.Tunnel.Access != DevTunnelAccessKind.Anonymous ||
            (request.Credential.AccountIdentifier is null && request.Tunnel.Endpoint is not null));

    private static bool IsRelayEndpoint(Uri endpoint) =>
        endpoint.IsAbsoluteUri && endpoint.Scheme == Uri.UriSchemeHttps &&
        endpoint.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase) &&
        endpoint.IsDefaultPort && endpoint.UserInfo.Length == 0 &&
        endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0;

    private GenerationCache GetCache(
        LogicalCredentialIdentifier logical,
        CredentialSourceIdentifier source)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            var key = new CacheKey(logical, source);
            if (!this.caches.TryGetValue(key, out var cache))
            {
                cache = new GenerationCache();
                this.caches.Add(key, cache);
            }
            return cache;
        }
    }

    private void RegistryChanged(object? sender, UserAuthorizationChangedEventArgs arguments)
    {
        var row = this.registry.GetSnapshot().SingleOrDefault(snapshot => snapshot.Identifier == arguments.Identifier);
        if (row is null || row.State is UserAuthorizationState.Completed or UserAuthorizationState.Failed or
            UserAuthorizationState.Denied or UserAuthorizationState.Cancelled)
        {
            lock (this.gate)
            {
                this.awaitingRenewal.Remove(arguments.Identifier);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
        }
        this.registry.Changed -= this.RegistryChanged;
        try
        {
            await this.lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (this.gate)
            {
                foreach (var cache in this.caches.Values)
                {
                    cache.Clear();
                }
                this.caches.Clear();
                this.administrationEntries.Clear();
                this.awaitingRenewal.Clear();
            }
            await this.session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record CacheKey(
        LogicalCredentialIdentifier Logical,
        CredentialSourceIdentifier Source);

    private sealed class Snapshot : IDisposable
    {
        public required char[] Material { get; init; }
        public required CredentialGenerationInspection Inspection { get; init; }
        public required Uri Endpoint { get; init; }
        public void Dispose() => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.Material.AsSpan()));
    }

    private sealed class GenerationCache : ICredentialGenerationCache
    {
        private readonly object gate = new();
        private Snapshot? current;

        public void Commit(
            DevTunnelConnectMintSucceeded minted,
            CredentialMaterialGenerationIdentifier generation)
        {
            lock (this.gate)
            {
                this.Clear();
                this.current = new Snapshot
                {
                    Material = minted.Material.ToArray(),
                    Endpoint = minted.Endpoint,
                    Inspection = new CredentialGenerationInspection
                    {
                        Generation = generation,
                        AccountIdentifier = minted.Account,
                        ExpiresOn = minted.ExpiresOn,
                    },
                };
            }
        }

        public Snapshot? Copy(CredentialMaterialGenerationIdentifier generation)
        {
            lock (this.gate)
            {
                return this.current?.Inspection.Generation == generation
                    ? new Snapshot
                    {
                        Material = this.current.Material.ToArray(),
                        Endpoint = this.current.Endpoint,
                        Inspection = this.current.Inspection,
                    }
                    : null;
            }
        }

        public ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                return ValueTask.FromResult(this.current?.Inspection);
            }
        }

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                return ValueTask.FromResult(this.current?.Inspection.Generation == generation);
            }
        }

        public ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                if (this.current is null)
                {
                    return ValueTask.FromResult(CredentialGenerationInvalidationOutcome.Missing);
                }
                if (this.current.Inspection.Generation != generation)
                {
                    return ValueTask.FromResult(CredentialGenerationInvalidationOutcome.NoLongerCurrent);
                }
                this.Clear();
                return ValueTask.FromResult(CredentialGenerationInvalidationOutcome.Invalidated);
            }
        }

        public void Clear()
        {
            lock (this.gate)
            {
                this.current?.Dispose();
                this.current = null;
            }
        }
    }

    private sealed class Acquisition(
        DevTunnelConnectCredentialAccessExecutor executor,
        DevTunnelConnectCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor source,
        GenerationCache cache,
        ProviderAcquisitionKey key,
        ICredentialExecutionContext context) : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.DevTunnelConnect;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;

        public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken)
        {
            if (!commitContext.InteractionPermit.AuthorizesProvider(this.ExecutorKind, key))
            {
                return new ProviderAcquisitionDenied();
            }
            var outcome = await executor.session.MintAsync(
                request, source, context, this.CompatibilityKey, commitContext.InteractionPermit, cancellationToken).ConfigureAwait(false);
            if (outcome is DevTunnelConnectMintDenied)
            {
                return new ProviderAcquisitionDenied();
            }
            if (outcome is DevTunnelConnectMintUnauthorized)
            {
                return new ProviderAcquisitionRenewalRequired();
            }
            if (outcome is not DevTunnelConnectMintSucceeded minted)
            {
                return new ProviderAcquisitionFailed
                {
                    ErrorCode = outcome is DevTunnelConnectMintFailed failed ? failed.ErrorCode : AccessErrorCode.ProviderFailure,
                };
            }
            using (minted)
            {
                if (minted.Material.Length == 0 || minted.ExpiresOn <= executor.timeProvider.GetUtcNow() ||
                    (request.Credential.AccountIdentifier is { } expected && expected != minted.Account) ||
                    (key.Account is { } approvedAccount && approvedAccount != minted.Account) ||
                    !IsRelayEndpoint(minted.Endpoint) ||
                    (request.Tunnel.Endpoint is { } endpoint &&
                        !string.Equals(endpoint.GetLeftPart(UriPartial.Authority),
                            minted.Endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)))
                {
                    return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
                }
                var generation = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
                return await commitContext.CommitAsync(generation, token =>
                {
                    token.ThrowIfCancellationRequested();
                    cache.Commit(minted, generation);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            }
        }
    }

    private sealed class AnonymousMaterializer(Uri endpoint) : IAccessResultMaterializer<DevTunnelConnectCredentialAccessResult>
    {
        public ValueTask<DevTunnelConnectCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DevTunnelConnectCredentialAccessResult(endpoint));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Materializer(
        Snapshot snapshot,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        GenerationCache cache,
        TimeProvider timeProvider) : IAccessResultMaterializer<DevTunnelConnectCredentialAccessResult>
    {
        public ValueTask<DevTunnelConnectCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new SensitiveValueLease(
                snapshot.Material,
                new CredentialLeaseBinding
                {
                    AuthorizationIdentifier = authorization.Identifier,
                    Request = context.PolicyRequest!,
                    MaterialGeneration = snapshot.Inspection.Generation,
                    ExpiresOn = snapshot.Inspection.ExpiresOn,
                },
                context.CreateLeaseContext(cache),
                timeProvider);
            return ValueTask.FromResult(new DevTunnelConnectCredentialAccessResult(
                lease, snapshot.Endpoint, cache, snapshot.Inspection.Generation));
        }

        public ValueTask DisposeAsync()
        {
            snapshot.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
