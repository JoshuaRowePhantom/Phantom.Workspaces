using System.Security;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// A persistent <see cref="ITokenCache"/> (sub-item #1384) that stores the MCP SDK's OAuth
/// <see cref="TokenContainer"/> in the existing per-user platform secret store
/// (<see cref="IPlatformSecretStore"/>, backed by Windows Credential Manager). The container is
/// serialized to JSON and persisted under a per-server key (<c>"mcp-oauth:" + serverName</c>) so a
/// restart can reuse a stored refresh token (silent refresh) instead of forcing a fresh interactive
/// sign-in. Registered into the #1382 <see cref="McpOAuthOptions.TokenCacheProvider"/> seam.
/// </summary>
/// <remarks>
/// Keying per MCP server ensures servers never share tokens. Plaintext JSON only ever crosses the
/// process boundary as a <see cref="SecureString"/>, marshalled via
/// <see cref="SecureStringMarshal.Use{T}(SecureString, System.Func{string, T})"/> exactly like the
/// rest of the secret store; the plaintext lifetime is bounded to the marshalling delegate.
/// </remarks>
public sealed class CredentialManagerTokenCache : ITokenCache
{
    /// <summary>Prefix applied to the per-server secret key so tokens live in their own namespace.</summary>
    internal const string KeyPrefix = "mcp-oauth:";

    private readonly IPlatformSecretStore store;
    private readonly string key;

    /// <summary>
    /// Creates a cache that persists <paramref name="serverName"/>'s tokens through
    /// <paramref name="store"/>.
    /// </summary>
    public CredentialManagerTokenCache(IPlatformSecretStore store, string serverName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(serverName);

        this.store = store;
        this.key = KeyPrefix + serverName;
    }

    /// <summary>
    /// Builds a <see cref="McpOAuthOptions.TokenCacheProvider"/> seam factory over
    /// <paramref name="store"/>. The returned factory yields a persistent cache per MCP server when a
    /// real secret store is available, and null (SDK in-memory fallback) when it is not — e.g. on
    /// non-Windows platforms where the host supplies a <see cref="NullPlatformSecretStore"/> or none.
    /// </summary>
    public static Func<string, ITokenCache?> CreateProvider(IPlatformSecretStore? store)
        => serverName => TokenCacheFor(store, serverName);

    /// <summary>
    /// Returns a persistent cache for <paramref name="serverName"/>, or null when
    /// <paramref name="store"/> is not a real persistent secret store (SDK in-memory fallback).
    /// </summary>
    public static ITokenCache? TokenCacheFor(IPlatformSecretStore? store, string serverName)
        => IsPersistent(store) ? new CredentialManagerTokenCache(store!, serverName) : null;

    /// <inheritdoc />
    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var json = JsonSerializer.Serialize(tokens);
        var secret = ToSecureString(json);
        await this.store.WriteAsync(this.key, secret, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var secret = await this.store.ReadAsync(this.key, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        return Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(
            secret, json => JsonSerializer.Deserialize<TokenContainer>(json));
    }

    /// <summary>
    /// Removes this server's cached tokens (sign-out / invalidation). A subsequent
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
