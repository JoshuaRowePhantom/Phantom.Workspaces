using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The concrete Wave-3 (#1458) <see cref="IDevTunnelConnectTokenProvider"/> that fills the #1456 handler's
/// token-provider seam. It uses the selected <see cref="IDevTunnelAuthTokenProvider"/> identity to
/// <b>mint a Connect-scope tunnel token at the Management API</b>, caches it in memory, and — when the
/// source is configured to <c>remember</c> — persists it via <see cref="CredentialManagerTokenCache"/>
/// under the <c>devtunnel:&lt;name&gt;</c> key so a remembered+cached identity connects <b>silently</b>
/// (no interactive prompt for the data-source connect).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-layer model (#1293).</b> The identity token authenticates only at the Management API to mint the
/// Connect token; the Connect token is the sole value ever sent to the relay as
/// <c>X-Tunnel-Authorization</c>. The identity token is never sent to the relay.
/// </para>
/// <para>
/// <b>Failure → refresh → re-auth.</b> A relay <c>401</c> is surfaced by the handler as
/// <see cref="RefreshConnectTokenAsync"/>: the cached Connect token is invalidated and re-minted. If the
/// Management-API mint call is itself unauthorized (identity expired) it throws
/// <see cref="DevTunnelManagementUnauthorizedException"/>, which triggers exactly one identity refresh via
/// the scheme provider before a single re-mint attempt — bounding an episode to one interactive prompt.
/// </para>
/// </remarks>
public sealed class ManagementApiDevTunnelConnectTokenProvider : IDevTunnelConnectTokenProvider
{
    /// <summary>The <see cref="CredentialManagerTokenCache"/> key prefix for dev-tunnel Connect tokens (#1454).</summary>
    public const string CacheKeyPrefix = "devtunnel:";

    private readonly IDevTunnelAuthTokenProvider identityProvider;
    private readonly Func<string, CancellationToken, Task<string?>> mintConnectTokenAsync;
    private readonly ITokenCache? persistentCache;
    private readonly IAllowedSecretsStore? consentStore;
    private readonly string cacheKey;
    private readonly bool remember;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);

    private string? connectToken;
    private bool hasConnectToken;

    /// <summary>
    /// Creates a Connect-token minting provider.
    /// </summary>
    /// <param name="identityProvider">Per-scheme identity that authenticates at the Management API.</param>
    /// <param name="mintConnectTokenAsync">
    /// Mints a Connect-scope tunnel token for the supplied identity token via the Management API. Must throw
    /// <see cref="DevTunnelManagementUnauthorizedException"/> when the identity token is unauthorized (so an
    /// identity refresh is triggered) and let network / 5xx failures propagate unchanged.
    /// </param>
    /// <param name="tunnelName">The tunnel name used as the persistent-cache key component (<c>devtunnel:&lt;name&gt;</c>).</param>
    /// <param name="remember">Whether to persist and silently reuse the minted Connect token.</param>
    /// <param name="persistentCache">Persistent Connect-token cache (normally a <see cref="CredentialManagerTokenCache"/>).</param>
    /// <param name="consentStore">Optional "remember" consent store; when supplied, silent persistent reuse requires a stored grant.</param>
    /// <param name="timeProvider">Clock used to stamp persisted tokens; defaults to <see cref="TimeProvider.System"/>.</param>
    public ManagementApiDevTunnelConnectTokenProvider(
        IDevTunnelAuthTokenProvider identityProvider,
        Func<string, CancellationToken, Task<string?>> mintConnectTokenAsync,
        string tunnelName,
        bool remember,
        ITokenCache? persistentCache = null,
        IAllowedSecretsStore? consentStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(mintConnectTokenAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelName);

        this.identityProvider = identityProvider;
        this.mintConnectTokenAsync = mintConnectTokenAsync;
        this.cacheKey = tunnelName;
        this.remember = remember;
        this.persistentCache = persistentCache;
        this.consentStore = consentStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The persistent-cache key (<c>devtunnel:&lt;name&gt;</c>) this provider stores its Connect token under.</summary>
    public string PersistentCacheKey => CacheKeyPrefix + this.cacheKey;

    /// <inheritdoc />
    public async ValueTask<string?> GetConnectTokenAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref this.hasConnectToken))
        {
            return this.connectToken;
        }

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.hasConnectToken)
            {
                return this.connectToken;
            }

            // Remembered + cached identity connects silently: reload the persisted Connect token without
            // acquiring the identity (no interactive prompt).
            var persisted = await this.TryLoadPersistedAsync(cancellationToken).ConfigureAwait(false);
            if (persisted is not null)
            {
                this.SetConnectToken(persisted);
                return persisted;
            }

            var minted = await this.MintWithIdentityRefreshAsync(forceIdentityRefresh: false, cancellationToken)
                .ConfigureAwait(false);
            await this.OnMintedAsync(minted, cancellationToken).ConfigureAwait(false);
            return minted;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> RefreshConnectTokenAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Invalidate the stale Connect token everywhere so it can never be reloaded/reused.
            this.connectToken = null;
            this.hasConnectToken = false;
            await this.ClearPersistedAsync(cancellationToken).ConfigureAwait(false);

            var minted = await this.MintWithIdentityRefreshAsync(forceIdentityRefresh: false, cancellationToken)
                .ConfigureAwait(false);
            await this.OnMintedAsync(minted, cancellationToken).ConfigureAwait(false);
            return minted;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Mints the Connect token for the current identity. On a
    /// <see cref="DevTunnelManagementUnauthorizedException"/> (identity expired/invalid) it refreshes the
    /// identity via the scheme provider exactly once — at most one interactive prompt — then re-mints once.
    /// </summary>
    private async Task<string?> MintWithIdentityRefreshAsync(bool forceIdentityRefresh, CancellationToken cancellationToken)
    {
        var identityToken = forceIdentityRefresh
            ? await this.identityProvider.RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false)
            : await this.identityProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await this.mintConnectTokenAsync(identityToken, cancellationToken).ConfigureAwait(false);
        }
        catch (DevTunnelManagementUnauthorizedException) when (!forceIdentityRefresh)
        {
            // The Management API rejected the identity token: refresh the identity (one prompt) and
            // re-mint exactly once. A second unauthorized result propagates rather than prompting again.
            var refreshedIdentity = await this.identityProvider.RefreshAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            return await this.mintConnectTokenAsync(refreshedIdentity, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnMintedAsync(string? minted, CancellationToken cancellationToken)
    {
        this.SetConnectToken(minted);
        if (minted is not null)
        {
            await this.PersistAsync(minted, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetConnectToken(string? token)
    {
        this.connectToken = token;
        Volatile.Write(ref this.hasConnectToken, true);
    }

    private async Task<string?> TryLoadPersistedAsync(CancellationToken cancellationToken)
    {
        if (!this.remember || this.persistentCache is null)
        {
            return null;
        }

        if (!await this.HasRememberedConsentAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var container = await this.persistentCache.GetTokensAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(container?.AccessToken) ? null : container.AccessToken;
    }

    private async Task PersistAsync(string token, CancellationToken cancellationToken)
    {
        if (!this.remember || this.persistentCache is null)
        {
            return;
        }

        await this.persistentCache.StoreTokensAsync(
            new TokenContainer
            {
                TokenType = "tunnel",
                AccessToken = token,
                ObtainedAt = this.timeProvider.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);

        await this.RememberConsentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearPersistedAsync(CancellationToken cancellationToken)
    {
        if (this.persistentCache is CredentialManagerTokenCache credentialCache)
        {
            await credentialCache.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> HasRememberedConsentAsync(CancellationToken cancellationToken)
    {
        if (this.consentStore is null)
        {
            return true;
        }

        var grant = await this.consentStore.TryGetAsync(this.ConsentHash, cancellationToken).ConfigureAwait(false);
        return grant is not null;
    }

    private async Task RememberConsentAsync(CancellationToken cancellationToken)
    {
        if (this.consentStore is null)
        {
            return;
        }

        var memory = new SecretUseMemory(SecretUseScope.AllUses, this.PersistentCacheKey, this.ConsentHash);
        var grant = new MemorizedSecret(memory, new OAuthSecretSource(), this.timeProvider.GetUtcNow());
        await this.consentStore.PutAsync(this.ConsentHash, grant, cancellationToken).ConfigureAwait(false);
    }

    private string ConsentHash
    {
        get
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(this.PersistentCacheKey));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
