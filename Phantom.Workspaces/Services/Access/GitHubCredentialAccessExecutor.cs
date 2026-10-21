using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Access;

internal sealed record GitHubPrivateCredentialRequest
{
    public required CredentialSourceKind Source { get; init; }
    public CredentialAccountIdentifier? AccountIdentifier { get; init; }
}

internal sealed record GitHubPrivateCredentialSource
{
    public required CredentialSourceIdentifier Identifier { get; init; }
    public required CredentialSourceKind Kind { get; init; }
    public CredentialAccountIdentifier? ExpectedAccountIdentifier { get; init; }
    public CredentialAccountIdentifier? RetainedAccountIdentifier { get; init; }
}

internal sealed record GitHubPrivateCredentialInspection
{
    public required GitHubPrivateCredentialSource Source { get; init; }
    public required ProviderAcquisitionKey AcquisitionKey { get; init; }
    public required CredentialSourceUsability Usability { get; init; }
    public CredentialGenerationInspection? CachedGeneration { get; init; }
}

internal abstract record GitHubPrivateCredentialUseOutcome<TResult>;
internal sealed record GitHubPrivateCredentialUseSucceeded<TResult> : GitHubPrivateCredentialUseOutcome<TResult>
{
    public required TResult Result { get; init; }
    public required CredentialMaterialGenerationIdentifier Generation { get; init; }
    public required CredentialAccountIdentifier AccountIdentifier { get; init; }
}
internal sealed record GitHubPrivateCredentialUseDenied<TResult> : GitHubPrivateCredentialUseOutcome<TResult>;
internal sealed record GitHubPrivateCredentialUseCancelled<TResult> : GitHubPrivateCredentialUseOutcome<TResult>;
internal sealed record GitHubPrivateCredentialUseFailed<TResult> : GitHubPrivateCredentialUseOutcome<TResult>
{
    public required AccessErrorCode ErrorCode { get; init; }
    public required bool CanRetry { get; init; }
}

internal sealed class GitHubCredentialMaterial : IDisposable
{
    public required char[] Material { get; init; }
    public required CredentialAccountIdentifier Account { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }

    public void Dispose() => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.Material.AsSpan()));
}

internal interface IGitHubCredentialAccessSource
{
    bool EnvironmentExists();

    ValueTask<GitHubCredentialMaterial?> AcquireAsync(
        CredentialSourceKind source,
        ProviderInteractionPermit permit,
        ProviderAcquisitionKey key,
        CancellationToken cancellationToken);
}

internal sealed class GitHubCredentialAccessExecutor : ICredentialAccessExecutor<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>, IAsyncDisposable, ICredentialCacheAdministration
{
    private readonly IGitHubCredentialAccessSource source;
    private readonly ExecutorCredentialCacheAdministration administration;
    private readonly Dictionary<CredentialSourceIdentifier, GenerationCache> caches = [];
    private readonly object gate = new();
    private bool disposed;

    public GitHubCredentialAccessExecutor()
        : this(new GitHubSource())
    {
    }

    internal GitHubCredentialAccessExecutor(IGitHubCredentialAccessSource source)
    {
        this.source = source;
        this.administration = new ExecutorCredentialCacheAdministration(
            CredentialProviderKind.GitHub,
            CredentialKind.AccessToken,
            this.InspectCachedGenerationsAsync,
            this.GetGenerationCache);
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.GitHub;

    public async ValueTask<CredentialInspection> InspectAsync(
        GitHubCredentialAccessRequest request,
        CancellationToken cancellationToken)
    {
        var sources = new List<CredentialSourceChoiceDescriptor>();
        var generations = new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>();
        var acquisitions = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>();
        var accounts = new List<CredentialAccountIdentifier?>();
        foreach (var choice in request.Sources)
        {
            if (!IsExactSource(request, choice))
            {
                continue;
            }
            var cache = this.GetCache(choice.Identifier);
            var metadata = await cache.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            var exists = choice.Kind != CredentialSourceKind.Environment || this.source.EnvironmentExists();
            if (!exists)
            {
                cache.Clear();
                metadata = null;
            }
            accounts.Add(cache.AccountIdentifier);
            sources.Add(choice with
            {
                Usability = !exists ? CredentialSourceUsability.Missing
                    : metadata is null ? CredentialSourceUsability.AcquisitionRequired : CredentialSourceUsability.Usable,
            });
            if (metadata is not null)
            {
                generations.Add(choice.Identifier, metadata);
            }
            acquisitions.Add(choice.Identifier, new ProviderAcquisitionKey
            {
                Configuration = request.Credential.LogicalIdentifier,
                Source = choice.Identifier,
                Account = request.Credential.AccountIdentifier ?? cache.AccountIdentifier,
                Protocol = ProviderProtocolKind.Cli,
            });
        }
        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential with
            {
                AccountIdentifier = request.Credential.AccountIdentifier ??
                    (accounts.Count > 0 && accounts.All(account => account is not null && account == accounts[0]) ? accounts[0] : null),
            },
            Sources = sources,
            CachedGenerations = generations,
            AcquisitionKeys = acquisitions,
        };
    }

    public async ValueTask<CredentialExecutionOutcome<GitHubCredentialAccessResult>> ExecuteAsync(
        GitHubCredentialAccessRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        var choice = inspection.Sources.SingleOrDefault(candidate => candidate.Identifier == inspection.SelectedSource);
        if (choice is null || !IsExactSource(request, choice) || context.PolicyRequest is not { } policyRequest ||
            policyRequest.SourceIdentifier != choice.Identifier ||
            policyRequest.RequestIdentifier != authorization.RequestIdentifier ||
            policyRequest.RequestGeneration != authorization.RequestGeneration)
        {
            return new CredentialExecutionDenied<GitHubCredentialAccessResult>();
        }
        // Unknown accounts are provisional until the exact approved flow resolves their identity.
        if (policyRequest.Credential.AccountIdentifier is not null &&
            await context.Policy.ValidateAsync(authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false) != ReleaseAuthorizationValidation.Valid)
        {
            return new CredentialExecutionDenied<GitHubCredentialAccessResult>();
        }

        var cache = this.GetCache(choice.Identifier);
        if (choice.Usability == CredentialSourceUsability.Missing ||
            choice.Kind == CredentialSourceKind.Environment && !this.source.EnvironmentExists())
        {
            cache.Clear();
            return new CredentialExecutionMissing<GitHubCredentialAccessResult>();
        }
        var metadata = await cache.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        var expectedAccount = request.Credential.AccountIdentifier ?? policyRequest.Credential.AccountIdentifier;
        if (metadata is not null && expectedAccount is not null && metadata.AccountIdentifier != expectedAccount)
        {
            return new CredentialExecutionFailed<GitHubCredentialAccessResult> { ErrorCode = AccessErrorCode.IdentityMismatch, CanRetry = false };
        }
        CredentialMaterialGenerationIdentifier generation;
        if (metadata is null)
        {
            if (interactionPermit is null)
            {
                return new CredentialExecutionInteractionRequired<GitHubCredentialAccessResult>();
            }
            var operation = new Acquisition(this, cache, choice.Kind, inspection.AcquisitionKeys[choice.Identifier], context);
            var acquired = await context.ExecuteProviderAcquisitionAsync(operation, interactionPermit, cancellationToken).ConfigureAwait(false);
            if (acquired is not ProviderAcquisitionSucceeded committed)
            {
                return acquired switch
                {
                    ProviderAcquisitionDenied => new CredentialExecutionDenied<GitHubCredentialAccessResult>(),
                    ProviderAcquisitionFailed failed => new CredentialExecutionFailed<GitHubCredentialAccessResult> { ErrorCode = failed.ErrorCode, CanRetry = true },
                    _ => new CredentialExecutionInteractionRequired<GitHubCredentialAccessResult>(),
                };
            }
            generation = committed.CommittedGeneration;
        }
        else
        {
            generation = metadata.Generation;
        }

        var snapshot = cache.Copy(generation);
        if (snapshot is null)
        {
            return new CredentialExecutionMissing<GitHubCredentialAccessResult>();
        }
        if ((request.Credential.AccountIdentifier ?? policyRequest.Credential.AccountIdentifier) is { } expected && expected != snapshot.Account)
        {
            snapshot.Dispose();
            return new CredentialExecutionFailed<GitHubCredentialAccessResult> { ErrorCode = AccessErrorCode.IdentityMismatch, CanRetry = false };
        }
        return new CredentialExecutionReady<GitHubCredentialAccessResult>
        {
            ResolvedIdentity = new ResolvedCredentialIdentity
            {
                RequestIdentifier = context.RequestIdentifier,
                RequestGeneration = context.Revision,
                LogicalIdentifier = request.Credential.LogicalIdentifier,
                SourceIdentifier = choice.Identifier,
                AccountIdentifier = snapshot.Account,
            },
            Materializer = new Materializer(snapshot, authorization, context, cache),
        };
    }

    internal ICredentialGenerationCache GetGenerationCache(CredentialSourceIdentifier source) => this.GetCache(source);

    public ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
        this.administration.ListAsync(cancellationToken);

    public ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default) =>
        this.administration.DeleteAsync(identifier, cancellationToken);

    internal async ValueTask<GitHubPrivateCredentialInspection> InspectPrivateAsync(
        GitHubPrivateCredentialRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var choice = GitHubCredentialAccessRequest.CreateSource(request.Source);
        var cache = this.GetCache(choice.Identifier);
        var exists = request.Source != CredentialSourceKind.Environment || this.source.EnvironmentExists();
        if (!exists)
        {
            cache.Clear();
        }
        var metadata = await cache.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        var retainedAccount = cache.AccountIdentifier;
        var source = new GitHubPrivateCredentialSource
        {
            Identifier = choice.Identifier,
            Kind = choice.Kind,
            ExpectedAccountIdentifier = request.AccountIdentifier ?? retainedAccount,
            RetainedAccountIdentifier = retainedAccount,
        };
        return new GitHubPrivateCredentialInspection
        {
            Source = source,
            AcquisitionKey = CreatePrivateKey(source),
            Usability = !exists ? CredentialSourceUsability.Missing
                : metadata is null || source.ExpectedAccountIdentifier is { } account && metadata.AccountIdentifier != account
                    ? CredentialSourceUsability.AcquisitionRequired
                    : CredentialSourceUsability.Usable,
            CachedGeneration = metadata,
        };
    }

    internal IProviderAcquisitionOperation CreatePrivateAcquisition(
        GitHubPrivateCredentialRequest request,
        GitHubPrivateCredentialSource source,
        ICredentialExecutionContext context)
    {
        if (context.PolicyRequest?.Credential is not { Provider: CredentialProviderKind.DevTunnel, Kind: CredentialKind.ConnectToken } ||
            !IsPrivateSource(source) || request.Source != source.Kind ||
            request.AccountIdentifier is { } expected && source.ExpectedAccountIdentifier != expected ||
            request.AccountIdentifier is null && source.ExpectedAccountIdentifier != source.RetainedAccountIdentifier)
        {
            throw new InvalidOperationException("The private GitHub acquisition does not match the Connect execution.");
        }
        var cache = this.GetCache(source.Identifier);
        if (!cache.MatchesRetainedAccount(source.RetainedAccountIdentifier))
        {
            throw new ReleaseIdentityMismatchException();
        }
        return new Acquisition(this, cache, source.Kind, CreatePrivateKey(source), context,
            new PrivateAccountBinding { RetainedAccountIdentifier = source.RetainedAccountIdentifier });
    }

    internal async ValueTask<GitHubPrivateCredentialUseOutcome<TResult>> UseGenerationAsync<TResult>(
        GitHubPrivateCredentialSource source,
        CredentialMaterialGenerationIdentifier generation,
        ICredentialExecutionContext context,
        ProviderInteractionPermit parentGenerationPermit,
        Func<string, CredentialAccountIdentifier, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApprovedConnectGeneration(context, parentGenerationPermit) || !IsPrivateSource(source))
        {
            return new GitHubPrivateCredentialUseDenied<TResult>();
        }
        var cache = this.GetCache(source.Identifier);
        if (source.Kind == CredentialSourceKind.Environment && !this.source.EnvironmentExists())
        {
            cache.Clear();
            return new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.InvalidGeneration, CanRetry = true };
        }
        var metadata = await cache.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.Generation != generation)
        {
            return new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.InvalidGeneration, CanRetry = true };
        }
        if (source.ExpectedAccountIdentifier is { } expected && metadata.AccountIdentifier != expected)
        {
            return new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.IdentityMismatch, CanRetry = false };
        }
        if (!IsApprovedConnectGeneration(context, parentGenerationPermit))
        {
            return new GitHubPrivateCredentialUseDenied<TResult>();
        }
        using var snapshot = cache.Copy(generation);
        if (snapshot is null || !await cache.IsCurrentGenerationAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            return new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.InvalidGeneration, CanRetry = true };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApprovedConnectGeneration(context, parentGenerationPermit))
        {
            return new GitHubPrivateCredentialUseDenied<TResult>();
        }
        return new GitHubPrivateCredentialUseSucceeded<TResult>
        {
            Result = await operation(new string(snapshot.Material), snapshot.Account, cancellationToken).ConfigureAwait(false),
            Generation = snapshot.Generation,
            AccountIdentifier = snapshot.Account,
        };
    }

    private static bool IsPrivateSource(GitHubPrivateCredentialSource source) =>
        source.Kind is CredentialSourceKind.Environment or CredentialSourceKind.GitHubCli &&
        source.Identifier == GitHubCredentialAccessRequest.CreateSource(source.Kind).Identifier;

    private static bool IsApprovedConnectGeneration(
        ICredentialExecutionContext context,
        ProviderInteractionPermit permit)
    {
        if (context.PolicyRequest is not { Credential.Provider: CredentialProviderKind.DevTunnel, Credential.Kind: CredentialKind.ConnectToken } request)
        {
            return false;
        }
        var key = new ProviderAcquisitionKey
        {
            Configuration = request.Credential.LogicalIdentifier,
            Source = request.SourceIdentifier,
            Account = request.Credential.AccountIdentifier,
            Protocol = ProviderProtocolKind.TunnelConnect,
        };
        return permit.AuthorizesProvider(CredentialExecutorKind.DevTunnelConnect, key) &&
            permit.Authorizes(context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect, context.AcquisitionGeneration, key);
    }

    private static ProviderAcquisitionKey CreatePrivateKey(GitHubPrivateCredentialSource source) => new()
    {
        Configuration = GitHubCredentialAccessRequest.CreateLogicalIdentifier(),
        Source = source.Identifier,
        Account = source.ExpectedAccountIdentifier,
        Protocol = ProviderProtocolKind.Cli,
    };

    internal async ValueTask<GitHubPrivateCredentialUseOutcome<TResult>> UsePrivateAsync<TResult>(
        GitHubPrivateCredentialRequest request,
        ICredentialExecutionContext context,
        ProviderInteractionPermit parentGenerationPermit,
        Func<string, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApprovedConnectGeneration(context, parentGenerationPermit) ||
            request.Source is not (CredentialSourceKind.Environment or CredentialSourceKind.GitHubCli))
        {
            return new GitHubPrivateCredentialUseDenied<TResult>();
        }

        var inspection = await this.InspectPrivateAsync(request, cancellationToken).ConfigureAwait(false);
        if (inspection.Usability == CredentialSourceUsability.Missing)
        {
            return new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.InvalidGeneration, CanRetry = true };
        }
        CredentialMaterialGenerationIdentifier generation;
        if (inspection.Usability != CredentialSourceUsability.Usable)
        {
            var acquisition = await context.ExecutePrivateProviderDependencyAsync(
                this.CreatePrivateAcquisition(request, inspection.Source, context),
                parentGenerationPermit,
                cancellationToken).ConfigureAwait(false);
            if (acquisition is not ProviderAcquisitionSucceeded committed)
            {
                return acquisition switch
                {
                    ProviderAcquisitionDenied => new GitHubPrivateCredentialUseDenied<TResult>(),
                    ProviderAcquisitionCancelled or ProviderAcquisitionShuttingDown => new GitHubPrivateCredentialUseCancelled<TResult>(),
                    ProviderAcquisitionFailed failed => new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = failed.ErrorCode, CanRetry = true },
                    _ => new GitHubPrivateCredentialUseFailed<TResult> { ErrorCode = AccessErrorCode.InvalidGeneration, CanRetry = true },
                };
            }
            generation = committed.CommittedGeneration;
        }
        else
        {
            generation = inspection.CachedGeneration!.Generation;
        }

        if (!IsApprovedConnectGeneration(context, parentGenerationPermit))
        {
            return new GitHubPrivateCredentialUseDenied<TResult>();
        }
        return await this.UseGenerationAsync(inspection.Source, generation, context, parentGenerationPermit,
            (material, _, token) => operation(material, token), cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyDictionary<CredentialSourceIdentifier, CredentialGenerationInspection>> InspectCachedGenerationsAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<CredentialSourceIdentifier, GenerationCache>[] snapshot;
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            snapshot = this.caches.ToArray();
        }
        var generations = new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>();
        foreach (var entry in snapshot)
        {
            if (await entry.Value.InspectGenerationAsync(cancellationToken).ConfigureAwait(false) is { } generation)
            {
                generations.Add(entry.Key, generation);
            }
        }
        return generations;
    }

    public ValueTask DisposeAsync()
    {
        lock (this.gate)
        {
            this.disposed = true;
            this.administration.Disable();
            foreach (var cache in this.caches.Values)
            {
                cache.Clear();
            }
            this.caches.Clear();
        }
        if (this.source is IDisposable disposable)
        {
            disposable.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private GenerationCache GetCache(CredentialSourceIdentifier source)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.caches.TryGetValue(source, out var cache))
            {
                cache = new GenerationCache();
                this.caches.Add(source, cache);
            }
            return cache;
        }
    }

    private static bool IsExactSource(
        GitHubCredentialAccessRequest request,
        CredentialSourceChoiceDescriptor source) =>
        request.Credential.Provider == CredentialProviderKind.GitHub &&
        request.Credential.Kind == CredentialKind.AccessToken &&
        request.Credential.LogicalIdentifier == GitHubCredentialAccessRequest.CreateLogicalIdentifier() &&
        source.Provider == CredentialProviderKind.GitHub &&
        source.Kind is CredentialSourceKind.Environment or CredentialSourceKind.GitHubCli &&
        source.Identifier == GitHubCredentialAccessRequest.CreateSource(source.Kind).Identifier;

    private sealed class Acquisition(
        GitHubCredentialAccessExecutor executor,
        GenerationCache cache,
        CredentialSourceKind source,
        ProviderAcquisitionKey key,
        ICredentialExecutionContext context,
        PrivateAccountBinding? privateAccount = null) : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.GitHub;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;

        public async ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken)
        {
            if (!commitContext.InteractionPermit.AuthorizesProvider(this.ExecutorKind, key))
            {
                return new ProviderAcquisitionDenied();
            }
            if (privateAccount is not null && !cache.MatchesRetainedAccount(privateAccount.RetainedAccountIdentifier))
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
            }
            try
            {
                using var acquired = await executor.source.AcquireAsync(source, commitContext.InteractionPermit, key, cancellationToken).ConfigureAwait(false);
                if (acquired is null)
                {
                    return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
                }
                if (key.Account is not null && key.Account != acquired.Account)
                {
                    return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
                }
                var generation = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
                return await commitContext.CommitAsync(generation, _ =>
                {
                    if (privateAccount is null)
                    {
                        cache.Commit(acquired, generation);
                    }
                    else
                    {
                        cache.CommitPrivate(acquired, generation, privateAccount);
                    }
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (ReleaseIdentityMismatchException)
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }
            catch (HttpRequestException)
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }
            catch (JsonException)
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }
        }
    }

    private sealed record PrivateAccountBinding
    {
        public CredentialAccountIdentifier? RetainedAccountIdentifier { get; init; }
    }

    private sealed class Snapshot : IDisposable
    {
        public required char[] Material { get; init; }
        public required CredentialMaterialGenerationIdentifier Generation { get; init; }
        public required CredentialAccountIdentifier Account { get; init; }
        public DateTimeOffset? ExpiresOn { get; init; }
        public void Dispose() => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(this.Material.AsSpan()));
    }

    private sealed class GenerationCache : ICredentialGenerationCache
    {
        private readonly object gate = new();
        private Snapshot? current;
        private CredentialAccountIdentifier? accountIdentifier;

        public CredentialAccountIdentifier? AccountIdentifier
        {
            get
            {
                lock (this.gate)
                {
                    return this.accountIdentifier;
                }
            }
        }

        public bool MatchesRetainedAccount(CredentialAccountIdentifier? account)
        {
            lock (this.gate)
            {
                return this.accountIdentifier == account;
            }
        }

        public void CommitPrivate(
            GitHubCredentialMaterial material,
            CredentialMaterialGenerationIdentifier generation,
            PrivateAccountBinding binding)
        {
            lock (this.gate)
            {
                if (this.accountIdentifier != binding.RetainedAccountIdentifier)
                {
                    throw new ReleaseIdentityMismatchException();
                }
                this.Commit(material, generation);
            }
        }

        public void Commit(
            GitHubCredentialMaterial material,
            CredentialMaterialGenerationIdentifier generation)
        {
            lock (this.gate)
            {
                this.Clear();
                this.accountIdentifier = material.Account;
                this.current = new Snapshot
                {
                    Material = material.Material.ToArray(),
                    Generation = generation,
                    Account = material.Account,
                    ExpiresOn = material.ExpiresOn,
                };
            }
        }

        public Snapshot? Copy(CredentialMaterialGenerationIdentifier generation)
        {
            lock (this.gate)
            {
                return this.current is not { } current || current.Generation != generation || current.ExpiresOn <= DateTimeOffset.UtcNow ? null : new Snapshot
                {
                    Material = current.Material.ToArray(),
                    Generation = current.Generation,
                    Account = current.Account,
                    ExpiresOn = current.ExpiresOn,
                };
            }
        }

        public ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                return ValueTask.FromResult(this.current is not { } current || current.ExpiresOn <= DateTimeOffset.UtcNow
                    ? null : new CredentialGenerationInspection
                    {
                        Generation = current.Generation,
                        AccountIdentifier = current.Account,
                        ExpiresOn = current.ExpiresOn,
                    });
            }
        }

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                return ValueTask.FromResult(this.current?.Generation == generation && !(this.current.ExpiresOn <= DateTimeOffset.UtcNow));
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
                if (this.current.Generation != generation)
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

    private sealed class Materializer(
        Snapshot snapshot,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        GenerationCache cache) : IAccessResultMaterializer<GitHubCredentialAccessResult>
    {
        public ValueTask<GitHubCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new GitHubCredentialAccessResult
            {
                Lease = new SensitiveValueLease(snapshot.Material, new CredentialLeaseBinding
                {
                    AuthorizationIdentifier = authorization.Identifier,
                    Request = context.PolicyRequest!,
                    MaterialGeneration = snapshot.Generation,
                    ExpiresOn = snapshot.ExpiresOn,
                }, context.CreateLeaseContext(cache)),
            });
        }

        public ValueTask DisposeAsync()
        {
            snapshot.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GitHubSource : IGitHubCredentialAccessSource, IDisposable
    {
        private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });

        public bool EnvironmentExists() => GetEnvironmentVariable("GITHUB_TOKEN", IntPtr.Zero, 0) > 0;

        public async ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            if (!permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key))
            {
                return null;
            }
            var token = source == CredentialSourceKind.Environment
                ? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                : await ReadCliTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("Phantom.Workspaces");
            using var response = await this.client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("id", out var identifier) || !identifier.TryGetInt64(out var account))
            {
                return null;
            }
            return new GitHubCredentialMaterial
            {
                Material = token.ToCharArray(),
                Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, account.ToString(CultureInfo.InvariantCulture)),
            };
        }

        public void Dispose() => this.client.Dispose();

        private static async Task<string?> ReadCliTokenAsync(CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("auth");
            process.StartInfo.ArgumentList.Add("token");
            process.StartInfo.ArgumentList.Add("--hostname");
            process.StartInfo.ArgumentList.Add("github.com");
            // A forced CLI choice must not be overridden by gh's environment-token precedence.
            process.StartInfo.Environment.Remove("GH_TOKEN");
            process.StartInfo.Environment.Remove("GITHUB_TOKEN");
            process.Start();
            using var cancelled = cancellationToken.Register(() =>
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            });
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            var token = await output.ConfigureAwait(false);
            return process.ExitCode == 0 ? token.Trim() : null;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetEnvironmentVariableW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetEnvironmentVariable(
            string name,
            IntPtr buffer,
            uint size);
    }
}
