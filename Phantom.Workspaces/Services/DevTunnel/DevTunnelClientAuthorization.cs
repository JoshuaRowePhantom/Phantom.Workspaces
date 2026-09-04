using System;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Decides the <c>X-Tunnel-Authorization</c> token a dev-tunnel client should send to the
/// <c>*.devtunnels.ms</c> relay. The relay only accepts a Microsoft-issued Connect-scope tunnel
/// access token; a GitHub OAuth identity token is always rejected with 401 (empty body). Issue
/// #1293 removes the pre-existing identity-token fallback that reversed the design point #19
/// assumption: for Private access without a Connect token, this class now fails fast with an
/// actionable error rather than sending a token guaranteed to 401. Anonymous access sends no
/// header at all.
/// </summary>
public static class DevTunnelClientAuthorization
{
    public static DevTunnelClientAuthorizationResult Resolve(
        DevTunnelEndpointResolution resolution,
        DevTunnelAccessMode accessMode)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        // An explicit Connect/Token-scope token (if the Management API minted one) is used verbatim.
        if (!string.IsNullOrEmpty(resolution.TunnelAuthToken))
        {
            return new DevTunnelClientAuthorizationResult(resolution.TunnelAuthToken, null);
        }

        // Anonymous access requires no tunnel-authorization header.
        if (accessMode == DevTunnelAccessMode.Anonymous)
        {
            return new DevTunnelClientAuthorizationResult(null, null);
        }

        // Private connect with no Connect token: the dev-tunnels relay rejects any GitHub identity
        // token, so we must not fall back to sending one. Fail fast with a message that names the
        // relay host and points at the missing Connect-scope tunnel token from the Management API
        // (typically caused by ownership or Workspaces-marker/name label mismatch).
        throw new InvalidOperationException(
            $"Dev tunnel private connect to '{resolution.BaseUri.Host}' requires a Connect-scope " +
            "tunnel access token, but the Management API did not return one for this identity. " +
            "Verify that the GitHub identity owns the tunnel and that the tunnel carries the " +
            "Workspaces marker label plus the expected tunnel-name label. Sending the GitHub " +
            "OAuth token to the dev-tunnels relay is not supported and will always fail with 401.");
    }
}

/// <summary>
/// The tunnel-authorization token and optional 401-refresh resolver a dev-tunnel client should send.
/// </summary>
public sealed record DevTunnelClientAuthorizationResult(string? Token, Func<string?>? RefreshResolver);
