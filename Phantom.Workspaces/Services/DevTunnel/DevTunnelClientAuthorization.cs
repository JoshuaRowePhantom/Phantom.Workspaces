using System;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Decides the <c>X-Tunnel-Authorization</c> token (and the 401-refresh resolver) a dev-tunnel client
/// should use for a resolved endpoint. Private connect is identity-derived (design point #19): when the
/// Management API returns no Connect token, the client authorizes with its GitHub identity token and a
/// refresh resolver — mirroring the explicit-access-point <c>UseGitHubAuthToken</c> path — rather than
/// sending no authorization header. Anonymous access sends no header at all.
/// </summary>
public static class DevTunnelClientAuthorization
{
    public static DevTunnelClientAuthorizationResult Resolve(
        DevTunnelEndpointResolution resolution,
        DevTunnelAccessMode accessMode,
        Func<string?> identityTokenResolver)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(identityTokenResolver);

        // An explicit Connect/Token-scope token (if the API minted one) is used verbatim.
        if (!string.IsNullOrEmpty(resolution.TunnelAuthToken))
        {
            return new DevTunnelClientAuthorizationResult(resolution.TunnelAuthToken, null);
        }

        // Anonymous access requires no tunnel-authorization header.
        if (accessMode == DevTunnelAccessMode.Anonymous)
        {
            return new DevTunnelClientAuthorizationResult(null, null);
        }

        // Private connect with no Connect token: authorize via the GitHub identity token and keep a
        // resolver so a 401 can refresh it.
        return new DevTunnelClientAuthorizationResult(identityTokenResolver(), () => identityTokenResolver());
    }
}

/// <summary>
/// The tunnel-authorization token and optional 401-refresh resolver a dev-tunnel client should send.
/// </summary>
public sealed record DevTunnelClientAuthorizationResult(string? Token, Func<string?>? RefreshResolver);
