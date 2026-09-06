using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.DevTunnels.Contracts;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Auth;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The <c>entra</c> dev-tunnel auth scheme (issue #1458): acquires the Management-API identity token from
/// Microsoft Entra by reusing the shared #1454 auth surface —
/// <see cref="EntraInteractiveCredentialFactory"/> builds the (host-pinned) <see cref="TokenCredential"/>
/// and <see cref="EntraPinnedTokenProvider"/> owns PKCE / silent refresh / single-flight. The
/// authority / tenant / client id / scopes are read from <see cref="RemoteAuthentication"/>. The token is
/// sent to the Management API under the <c>aad</c> scheme; it is <b>never</b> sent to the relay (#1293).
/// </summary>
public sealed class EntraDevTunnelAuthProvider : IDevTunnelAuthTokenProvider
{
    /// <summary>
    /// The default Entra authority host used when only a tenant id (and no full authority) is configured.
    /// </summary>
    private const string DefaultAuthorityHost = "https://login.microsoftonline.com/";

    /// <summary>The Management-API resource scope used when no explicit scopes are configured.</summary>
    private const string DefaultScope = "https://tunnels.api.visualstudio.com/.default";

    private readonly EntraPinnedTokenProvider tokenProvider;

    /// <summary>
    /// Creates an Entra identity provider from <paramref name="authentication"/>. The
    /// <paramref name="credentialFactory"/> seam builds the underlying <see cref="TokenCredential"/> —
    /// defaulting to <see cref="EntraInteractiveCredentialFactory.Create(EntraPinnedTokenRequest, ILoggerFactory?)"/>;
    /// headless/unit contexts inject a fake credential so no MSAL dependency is taken.
    /// </summary>
    public EntraDevTunnelAuthProvider(
        RemoteAuthentication authentication,
        Func<EntraPinnedTokenRequest, TokenCredential>? credentialFactory = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(authentication);

        var request = new EntraPinnedTokenRequest(
            Authority: ResolveAuthority(authentication),
            ClientId: authentication.ClientId,
            RedirectUri: null,
            CallerName: authentication.TokenCacheKey ?? "devtunnel");

        var factory = credentialFactory ?? (r => EntraInteractiveCredentialFactory.Create(r, loggerFactory));
        var credential = factory(request);

        this.tokenProvider = new EntraPinnedTokenProvider(
            credential,
            ResolveScopes(authentication.Scopes),
            timeProvider,
            loggerFactory?.CreateLogger<EntraDevTunnelAuthProvider>());
    }

    /// <summary>The Microsoft Entra (Azure AD) Management-API authentication scheme.</summary>
    public string? AuthenticationScheme => TunnelAuthenticationSchemes.Aad;

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => await this.tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

    private static string ResolveAuthority(RemoteAuthentication authentication)
    {
        if (!string.IsNullOrWhiteSpace(authentication.Authority))
        {
            return authentication.Authority;
        }

        if (!string.IsNullOrWhiteSpace(authentication.TenantId))
        {
            return $"{DefaultAuthorityHost}{authentication.TenantId}/v2.0";
        }

        return $"{DefaultAuthorityHost}organizations/v2.0";
    }

    private static IEnumerable<string> ResolveScopes(IReadOnlyList<string>? scopes)
        => scopes is { Count: > 0 } configured ? configured.ToArray() : new[] { DefaultScope };
}
