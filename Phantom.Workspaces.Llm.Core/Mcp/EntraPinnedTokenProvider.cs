using System.Collections.Generic;
using System.Linq;
using Azure.Core;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Acquires and caches Microsoft Entra access tokens for host-pinned (<c>entra-pinned</c>) MCP OAuth
/// (issue #1420). It wraps a first-party <see cref="TokenCredential"/> (normally
/// <c>Azure.Identity.InteractiveBrowserCredential</c>), which authenticates with the v2 <c>scope</c>
/// parameter — <b>not</b> the RFC 8707 <c>resource</c> indicator — side-stepping the audience-mismatch
/// rejection that motivated this mode. The credential library owns PKCE, silent refresh, and the
/// persistent cache; this type adds only an in-memory single-flight guard so parallel first-time
/// requests coalesce into a single interactive prompt.
/// </summary>
public sealed class EntraPinnedTokenProvider
{
    /// <summary>
    /// Safety margin subtracted from a token's expiry before it is considered reusable, so a token is
    /// refreshed slightly early rather than handed out moments before it expires.
    /// </summary>
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly TokenCredential credential;
    private readonly string[] scopes;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim acquisitionGate = new(1, 1);

    private AccessToken? cachedToken;

    /// <summary>
    /// Creates a provider that acquires tokens for <paramref name="scopes"/> through
    /// <paramref name="credential"/>.
    /// </summary>
    /// <param name="credential">The first-party credential that performs the actual acquisition.</param>
    /// <param name="scopes">The statically configured v2 scopes; never sourced from remote metadata.</param>
    /// <param name="timeProvider">Clock used for cache-expiry decisions; defaults to <see cref="TimeProvider.System"/>.</param>
    public EntraPinnedTokenProvider(
        TokenCredential credential,
        IEnumerable<string> scopes,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(scopes);

        this.credential = credential;
        this.scopes = scopes.ToArray();
        if (this.scopes.Length == 0)
        {
            throw new ArgumentException("At least one scope is required for host-pinned Entra authentication.", nameof(scopes));
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a valid access token, reusing the cached token while it remains valid and otherwise
    /// acquiring a new one. Concurrent first-time callers are serialized so only one acquisition (and
    /// thus at most one interactive prompt) occurs.
    /// </summary>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (this.TryGetCachedToken(out var token))
        {
            return token;
        }

        await this.acquisitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: a peer may have populated the cache while we waited.
            if (this.TryGetCachedToken(out token))
            {
                return token;
            }

            var context = new TokenRequestContext(this.scopes);
            var acquired = await this.credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            this.cachedToken = acquired;
            return acquired.Token;
        }
        finally
        {
            this.acquisitionGate.Release();
        }
    }

    private bool TryGetCachedToken(out string token)
    {
        if (this.cachedToken is { } cached && this.timeProvider.GetUtcNow() < cached.ExpiresOn - ExpiryBuffer)
        {
            token = cached.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }
}
