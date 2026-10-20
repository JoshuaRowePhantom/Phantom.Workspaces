using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Auth;

/// <summary>
/// A persistent <see cref="ITokenCache"/> (sub-item #1384) that stores an OAuth
/// <see cref="TokenContainer"/> in the existing per-user platform secret store
/// (<see cref="IPlatformSecretStore"/>, backed by Windows Credential Manager). The container is
/// serialized to JSON and persisted under a versioned per-caller key (<c>keyPrefix + "v2:" + cacheKey</c>) so a
/// restart can reuse a stored refresh token (silent refresh) instead of forcing a fresh interactive
/// sign-in. Registered into the #1382 <see cref="InteractiveOAuthOptions.TokenCacheProvider"/> seam.
/// The key prefix is caller-supplied (issue #1454): MCP passes <c>mcp-oauth:</c>, dev-tunnel passes
/// <c>devtunnel:</c>.
/// </summary>
/// <remarks>
/// Keying per caller ensures callers never share tokens. Plaintext JSON only ever crosses the
/// process boundary as a <see cref="SecureString"/>, marshalled via
/// <see cref="SecureStringMarshal.Use{T}(SecureString, System.Func{string, T})"/> exactly like the
/// rest of the secret store; the plaintext lifetime is bounded to the marshalling delegate.
/// The private cache record persists its exact material generation with the tokens under
/// <c>keyPrefix + "v2:" + cacheKey</c>. Obsolete unversioned records are neither read nor migrated.
/// Safe in-process metadata is shared by store/partition; cold-start inspection remains unknown until an authorized
/// read or acquisition, rather than opening Credential Manager during metadata inspection.
/// </remarks>
public sealed class CredentialManagerTokenCache : ITokenCache, ICredentialGenerationCache
{
    private const string StorageNamespace = "v2:";
    private static readonly ConditionalWeakTable<IPlatformSecretStore, ConcurrentDictionary<string, CachePartition>> partitions = new();
    private readonly IPlatformSecretStore store;
    private readonly string key;
    private readonly ILogger logger;
    private readonly CachePartition partition;

    /// <summary>
    /// Creates a cache that persists <paramref name="cacheKey"/>'s tokens through
    /// <paramref name="store"/> under the caller-supplied <paramref name="keyPrefix"/> namespace. The
    /// optional <paramref name="logger"/> records only safe cache outcomes at Debug.
    /// </summary>
    public CredentialManagerTokenCache(IPlatformSecretStore store, string keyPrefix, string cacheKey, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);

        this.store = store;
        this.key = keyPrefix + StorageNamespace + cacheKey;
        this.partition = partitions.GetValue(store, _ => new(StringComparer.Ordinal))
            .GetOrAdd(this.key, _ => new CachePartition());
        this.logger = logger ?? NullLogger<CredentialManagerTokenCache>.Instance;
    }

    /// <summary>
    /// Builds an <see cref="InteractiveOAuthOptions.TokenCacheProvider"/> seam factory over
    /// <paramref name="store"/>, keyed under the caller-supplied <paramref name="keyPrefix"/>. The
    /// returned factory yields a persistent cache per caller when a real secret store is available,
    /// and null (SDK in-memory fallback) when it is not — e.g. on non-Windows platforms where the host
    /// supplies a <see cref="NullPlatformSecretStore"/> or none.
    /// </summary>
    public static Func<string, ITokenCache?> CreateProvider(IPlatformSecretStore? store, string keyPrefix, ILoggerFactory? loggerFactory = null)
        => cacheKey => TokenCacheFor(store, keyPrefix, cacheKey, loggerFactory);

    /// <summary>
    /// Returns a persistent cache for <paramref name="cacheKey"/> under <paramref name="keyPrefix"/>,
    /// or null when <paramref name="store"/> is not a real persistent secret store (SDK in-memory
    /// fallback).
    /// </summary>
    public static ITokenCache? TokenCacheFor(IPlatformSecretStore? store, string keyPrefix, string cacheKey, ILoggerFactory? loggerFactory = null)
        => IsPersistent(store)
            ? new CredentialManagerTokenCache(store!, keyPrefix, cacheKey, loggerFactory?.CreateLogger<CredentialManagerTokenCache>())
            : null;

    /// <inheritdoc />
    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) =>
        this.WriteGenerationAsync(tokens, new() { Value = Guid.NewGuid() }, false, cancellationToken);

    internal ValueTask StoreGenerationAsync(
        TokenContainer tokens,
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken) =>
        this.WriteGenerationAsync(tokens, generation, true, cancellationToken);

    internal async ValueTask ReserveGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.partition.PendingGeneration = generation;
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    private async ValueTask WriteGenerationAsync(
        TokenContainer tokens,
        CredentialMaterialGenerationIdentifier generation,
        bool requiresReservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var metadata = Metadata(tokens, generation);
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requiresReservation && this.partition.PendingGeneration != generation)
            {
                throw new InvalidOperationException("OAuth cache generation was superseded.");
            }

            this.partition.PendingGeneration = generation;
            // A failed platform write may have changed the stored bytes. Never keep the old
            // generation valid across an attempted replacement, even when that write fails.
            this.partition.Metadata = null;
            using var secret = ToSecureString(JsonSerializer.Serialize(new CachedTokens
            {
                Generation = generation,
                Tokens = tokens,
            }));
            await this.store.WriteAsync(this.key, secret, cancellationToken).ConfigureAwait(false);
            this.partition.Metadata = metadata;
            this.logger.LogDebug("Stored OAuth tokens in the persistent cache.");
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.ReadTokensAsync(null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    internal async ValueTask<TokenContainer?> GetGenerationTokensAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return this.partition.Metadata?.Generation == generation
                ? await this.ReadTokensAsync(generation, cancellationToken).ConfigureAwait(false)
                : null;
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    private async ValueTask<TokenContainer?> ReadTokensAsync(
        CredentialMaterialGenerationIdentifier? expectedGeneration,
        CancellationToken cancellationToken)
    {
        using var secret = await this.store.ReadAsync(this.key, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            this.partition.Metadata = null;
            this.logger.LogDebug("No cached OAuth tokens; interactive login is required.");
            return null;
        }

        this.logger.LogDebug("Loaded cached OAuth tokens (cache hit).");

        this.partition.Metadata = null;
        var cached = Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(
            secret, json => JsonSerializer.Deserialize<CachedTokens>(json));
        this.partition.Metadata = cached is null ? null : Metadata(cached.Tokens, cached.Generation);
        return cached is not null && (expectedGeneration is null || cached.Generation == expectedGeneration)
            ? cached.Tokens
            : null;
    }

    /// <summary>
    /// Removes this caller's cached tokens (sign-out / invalidation). A subsequent
    /// <see cref="GetTokensAsync"/> returns null.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.partition.Metadata = null;
            this.partition.PendingGeneration = null;
            await this.store.DeleteAsync(this.key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    async ValueTask<CredentialGenerationInspection?> ICredentialGenerationCache.InspectGenerationAsync(
        CancellationToken cancellationToken)
    {
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Cold-start metadata is deliberately unknown; inspection never reads stored material.
            return this.partition.Metadata;
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    async ValueTask<bool> ICredentialGenerationValidator.IsCurrentGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        var metadata = await ((ICredentialGenerationCache)this).InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        return metadata?.Generation == generation &&
            (metadata.ExpiresOn is null || metadata.ExpiresOn > DateTimeOffset.UtcNow);
    }

    async ValueTask<CredentialGenerationInvalidationOutcome> ICredentialGenerationCache.InvalidateGenerationAsync(
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        await this.partition.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.partition.Metadata is null)
            {
                return CredentialGenerationInvalidationOutcome.Missing;
            }
            if (this.partition.Metadata.Generation != generation)
            {
                return CredentialGenerationInvalidationOutcome.NoLongerCurrent;
            }

            // Check the generation in the authoritative private record too. Another owner may have
            // replaced the platform entry since this process last published safe metadata.
            if (await this.ReadTokensAsync(generation, cancellationToken).ConfigureAwait(false) is null)
            {
                return this.partition.Metadata is null
                    ? CredentialGenerationInvalidationOutcome.Missing
                    : CredentialGenerationInvalidationOutcome.NoLongerCurrent;
            }

            this.partition.Metadata = null;
            await this.store.DeleteAsync(this.key, cancellationToken).ConfigureAwait(false);
            return CredentialGenerationInvalidationOutcome.Invalidated;
        }
        finally
        {
            this.partition.Gate.Release();
        }
    }

    private static bool IsPersistent(IPlatformSecretStore? store)
        => store is not null and not NullPlatformSecretStore;

    private static CredentialGenerationInspection Metadata(
        TokenContainer tokens,
        CredentialMaterialGenerationIdentifier generation) => new()
        {
            Generation = generation,
            ExpiresOn = tokens.ExpiresIn is { } seconds ? tokens.ObtainedAt.AddSeconds(seconds) : null,
        };

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
        {
            secure.AppendChar(character);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private sealed class CachePartition
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CredentialGenerationInspection? Metadata { get; set; }
        internal CredentialMaterialGenerationIdentifier? PendingGeneration { get; set; }
    }

    private sealed record CachedTokens
    {
        public required CredentialMaterialGenerationIdentifier Generation { get; init; }
        public required TokenContainer Tokens { get; init; }
    }
}
