using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Access;

internal interface IStoredSecretAccessSource
{
    ValueTask<bool> ExistsAsync(
        string name,
        CredentialSourceKind source,
        CancellationToken cancellationToken);

    ValueTask<char[]?> ReadAsync(
        string name,
        CredentialSourceKind source,
        CancellationToken cancellationToken);
}

internal sealed class StoredSecretAccessExecutor : ICredentialAccessExecutor<StoredSecretAccessRequest, StoredSecretAccessResult>, IAsyncDisposable, ICredentialCacheAdministration
{
    private readonly IStoredSecretAccessSource source;
    private readonly ExecutorCredentialCacheAdministration administration;
    private readonly Dictionary<CredentialSourceIdentifier, GenerationCache> caches = [];
    private readonly object gate = new();
    private bool disposed;

    public StoredSecretAccessExecutor(IPlatformSecretStore store)
        : this(new PlatformSource(store))
    {
    }

    internal StoredSecretAccessExecutor(IStoredSecretAccessSource source)
    {
        this.source = source;
        this.administration = new ExecutorCredentialCacheAdministration(
            CredentialProviderKind.StoredSecret,
            CredentialKind.Secret,
            this.InspectCachedGenerationsAsync,
            this.GetGenerationCache);
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.StoredSecret;

    public async ValueTask<CredentialInspection> InspectAsync(
        StoredSecretAccessRequest request,
        CancellationToken cancellationToken)
    {
        var sources = new List<CredentialSourceChoiceDescriptor>();
        var generations = new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>();
        foreach (var choice in request.Sources)
        {
            if (!IsExactSource(request, choice))
            {
                continue;
            }

            var exists = await this.source.ExistsAsync(request.Reference.Name, choice.Kind, cancellationToken).ConfigureAwait(false);
            sources.Add(choice with { Usability = exists ? CredentialSourceUsability.Usable : CredentialSourceUsability.Missing });
            if (!exists)
            {
                this.GetCache(choice.Identifier).Clear();
            }
            var metadata = await this.GetCache(choice.Identifier).InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is not null)
            {
                generations[choice.Identifier] = metadata;
            }
        }

        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential,
            Sources = sources,
            CachedGenerations = generations,
        };
    }

    public async ValueTask<CredentialExecutionOutcome<StoredSecretAccessResult>> ExecuteAsync(
        StoredSecretAccessRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        var choice = inspection.Sources.SingleOrDefault(candidate => candidate.Identifier == inspection.SelectedSource);
        if (choice is null || !IsExactSource(request, choice) || context.PolicyRequest is not { } policyRequest ||
            policyRequest.SourceIdentifier != choice.Identifier ||
            await context.Policy.ValidateAsync(authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false) != ReleaseAuthorizationValidation.Valid)
        {
            return new CredentialExecutionDenied<StoredSecretAccessResult>();
        }

        var cache = this.GetCache(choice.Identifier);
        char[]? material = null;
        try
        {
            material = await this.source.ReadAsync(request.Reference.Name, choice.Kind, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return new CredentialExecutionFailed<StoredSecretAccessResult> { ErrorCode = AccessErrorCode.Disposed, CanRetry = false };
                }
            }
            var validation = await context.Policy.ValidateAsync(authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
            lock (this.gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (this.disposed)
                {
                    return new CredentialExecutionFailed<StoredSecretAccessResult> { ErrorCode = AccessErrorCode.Disposed, CanRetry = false };
                }
                if (validation != ReleaseAuthorizationValidation.Valid)
                {
                    return new CredentialExecutionDenied<StoredSecretAccessResult>();
                }
                if (material is null)
                {
                    cache.Clear();
                    return new CredentialExecutionMissing<StoredSecretAccessResult>();
                }
                var generation = cache.Commit(material);
                return new CredentialExecutionReady<StoredSecretAccessResult>
                {
                    Materializer = new Materializer(material, generation, authorization, context, cache),
                };
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CredentialExecutionFailed<StoredSecretAccessResult> { ErrorCode = AccessErrorCode.ProviderFailure, CanRetry = true };
        }
        finally
        {
            if (material is not null)
            {
                Zero(material);
            }
        }
    }

    internal ICredentialGenerationCache GetGenerationCache(CredentialSourceIdentifier source) => this.GetCache(source);

    public ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
        this.administration.ListAsync(cancellationToken);

    public ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default) =>
        this.administration.DeleteAsync(identifier, cancellationToken);

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
        StoredSecretAccessRequest request,
        CredentialSourceChoiceDescriptor source) =>
        request.Credential.Provider == CredentialProviderKind.StoredSecret &&
        request.Credential.Kind == CredentialKind.Secret &&
        source.Provider == CredentialProviderKind.StoredSecret &&
        source.Kind is CredentialSourceKind.Environment or CredentialSourceKind.SecretStore &&
        source.Identifier == CredentialIdentityFactory.CreateSourceIdentifier(
            CredentialProviderKind.StoredSecret,
            $"{source.Kind}:{request.Reference.Name}");

    private static void Zero(char[] material) => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(material.AsSpan()));

    private sealed class PlatformSource(IPlatformSecretStore store) : IStoredSecretAccessSource
    {
        public async ValueTask<bool> ExistsAsync(
            string name,
            CredentialSourceKind source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source == CredentialSourceKind.Environment)
            {
                return GetEnvironmentVariable(name, IntPtr.Zero, 0) > 0;
            }
            return (await store.EnumerateNamesAsync(name, cancellationToken).ConfigureAwait(false)).Contains(name, StringComparer.Ordinal);
        }

        public async ValueTask<char[]?> ReadAsync(
            string name,
            CredentialSourceKind source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source == CredentialSourceKind.Environment)
            {
                return Environment.GetEnvironmentVariable(name)?.ToCharArray();
            }
            using var secure = await store.ReadAsync(name, cancellationToken).ConfigureAwait(false);
            if (secure is null)
            {
                return null;
            }
            var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
            try
            {
                var characters = new char[secure.Length];
                Marshal.Copy(pointer, characters, 0, characters.Length);
                return characters;
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetEnvironmentVariableW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetEnvironmentVariable(
            string name,
            IntPtr buffer,
            uint size);
    }

    private sealed class GenerationCache : ICredentialGenerationCache
    {
        private readonly object gate = new();
        private char[]? material;
        private CredentialMaterialGenerationIdentifier? generation;

        public CredentialMaterialGenerationIdentifier Commit(char[] material)
        {
            lock (this.gate)
            {
                if (this.material is null || !this.material.AsSpan().SequenceEqual(material))
                {
                    this.Clear();
                    this.material = material.ToArray();
                    this.generation = new() { Value = Guid.NewGuid() };
                }
                return this.generation!;
            }
        }

        public ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                return ValueTask.FromResult(this.generation is null ? null : new CredentialGenerationInspection { Generation = this.generation });
            }
        }

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                return ValueTask.FromResult(this.generation == generation);
            }
        }

        public ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.gate)
            {
                if (this.generation is null)
                {
                    return ValueTask.FromResult(CredentialGenerationInvalidationOutcome.Missing);
                }
                if (this.generation != generation)
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
                if (this.material is not null)
                {
                    Zero(this.material);
                }
                this.material = null;
                this.generation = null;
            }
        }
    }

    private sealed class Materializer(
        char[] material,
        CredentialMaterialGenerationIdentifier generation,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        GenerationCache cache) : IAccessResultMaterializer<StoredSecretAccessResult>
    {
        private readonly char[] material = material.ToArray();

        public ValueTask<StoredSecretAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StoredSecretAccessResult
            {
                Lease = new SensitiveValueLease(this.material, new CredentialLeaseBinding
                {
                    AuthorizationIdentifier = authorization.Identifier,
                    Request = context.PolicyRequest!,
                    MaterialGeneration = generation,
                }, context.CreateLeaseContext(cache)),
            });
        }

        public ValueTask DisposeAsync()
        {
            Zero(this.material);
            return ValueTask.CompletedTask;
        }
    }
}
