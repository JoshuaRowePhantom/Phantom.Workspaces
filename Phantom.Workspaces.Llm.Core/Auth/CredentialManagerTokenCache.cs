using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Auth;

/// <summary>
/// A persistent <see cref="ITokenCache"/> (sub-item #1384) that stores an OAuth
/// <see cref="TokenContainer"/> in the existing per-user platform secret store
/// (<see cref="IPlatformSecretStore"/>, backed by Windows Credential Manager). The container is
/// serialized to JSON and persisted under a per-caller key (<c>keyPrefix + cacheKey</c>) so a
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
/// </remarks>
public sealed class CredentialManagerTokenCache : ITokenCache
{
    private readonly IPlatformSecretStore store;
    private readonly string key;
    private readonly string cacheKey;
    private readonly ILogger logger;

    /// <summary>
    /// Creates a cache that persists <paramref name="cacheKey"/>'s tokens through
    /// <paramref name="store"/> under the caller-supplied <paramref name="keyPrefix"/> namespace. The
    /// optional <paramref name="logger"/> records cache hits/misses at Debug — only the cache key is
    /// ever logged, never a token value (#1446/#1408 redaction).
    /// </summary>
    public CredentialManagerTokenCache(IPlatformSecretStore store, string keyPrefix, string cacheKey, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);

        this.store = store;
        this.cacheKey = cacheKey;
        this.key = keyPrefix + cacheKey;
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
    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var json = JsonSerializer.Serialize(tokens);
        var secret = ToSecureString(json);
        await this.store.WriteAsync(this.key, secret, cancellationToken).ConfigureAwait(false);

        // Redaction (#1446/#1408): record only that tokens were cached and for which caller — never
        // the serialized token value, access/refresh token, or scope values.
        this.logger.LogDebug(
            "Stored OAuth tokens in the persistent cache for '{CacheKey}'.", this.cacheKey);
    }

    /// <inheritdoc />
    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var secret = await this.store.ReadAsync(this.key, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            // A cache miss means there is no stored refresh token, so a fresh interactive login is
            // required. Only the cache key is logged (#1446/#1408).
            this.logger.LogDebug(
                "No cached OAuth tokens for '{CacheKey}'; interactive login is required.",
                this.cacheKey);
            return null;
        }

        this.logger.LogDebug(
            "Loaded cached OAuth tokens for '{CacheKey}' (cache hit).", this.cacheKey);

        return Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(
            secret, json => JsonSerializer.Deserialize<TokenContainer>(json));
    }

    /// <summary>
    /// Removes this caller's cached tokens (sign-out / invalidation). A subsequent
    /// <see cref="GetTokensAsync"/> returns null.
    /// </summary>
    public Task ClearAsync(CancellationToken cancellationToken)
        => this.store.DeleteAsync(this.key, cancellationToken);

    private static bool IsPersistent(IPlatformSecretStore? store)
        => store is not null and not NullPlatformSecretStore;

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
}
