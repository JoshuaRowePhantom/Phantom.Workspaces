using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Contracts;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Supplies the dev-tunnel <b>Management-API</b> identity token used on both ends of a tunnel: the host
/// (to create/own/host its tunnel) and a connecting client (to mint a Connect-scope token for a Private
/// tunnel). The concrete implementation is selected per <c>RemoteAuthentication.Scheme</c> (issue #1458):
/// <c>github</c>, <c>entra</c>, <c>oauth</c> or <c>anonymous</c>. Performs an interactive sign-in once and
/// caches/refreshes the token; a raw token is never stored in tracked files.
/// </summary>
/// <remarks>
/// This is the <b>identity</b> layer only. The identity token authenticates at the Management API to mint
/// a <b>Connect-scope</b> tunnel token; that Connect token — never the identity token — is what the relay
/// accepts as <c>X-Tunnel-Authorization</c> (#1293). Connect-token minting/caching lives in
/// <see cref="ManagementApiDevTunnelConnectTokenProvider"/>.
/// </remarks>
public interface IDevTunnelAuthTokenProvider
{
    /// <summary>
    /// The Dev Tunnels SDK authentication scheme this identity uses on the Management-API header
    /// (e.g. <c>github</c> or <c>aad</c>), or <see langword="null"/> for anonymous access (no header).
    /// Defaults to <see cref="TunnelAuthenticationSchemes.GitHub"/> so existing implementers are
    /// unchanged.
    /// </summary>
    string? AuthenticationScheme => TunnelAuthenticationSchemes.GitHub;

    /// <summary>
    /// Returns a valid management access token, signing in interactively or refreshing a cached token
    /// as needed. Anonymous providers return an empty string (paired with a <see langword="null"/>
    /// <see cref="AuthenticationScheme"/>).
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces acquisition of a fresh identity token, discarding any cached value. Invoked when the
    /// Management API rejects the current identity token as expired/invalid. Defaults to
    /// <see cref="GetAccessTokenAsync"/> for providers that refresh transparently.
    /// </summary>
    Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
        => this.GetAccessTokenAsync(cancellationToken);
}
