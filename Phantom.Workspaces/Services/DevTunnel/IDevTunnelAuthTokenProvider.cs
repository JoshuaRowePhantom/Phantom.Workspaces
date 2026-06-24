using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Supplies the dev-tunnel management identity token used on both ends of a tunnel: the host (to
/// create/own/host its tunnel) and a connecting client (to reach a Private tunnel). Performs an
/// interactive sign-in once and caches/refreshes the token in the OS secret store. It does not read an
/// externally-provisioned token, and a raw token is never stored in tracked files.
/// </summary>
public interface IDevTunnelAuthTokenProvider
{
    /// <summary>
    /// Returns a valid management access token, signing in interactively or refreshing a cached token
    /// as needed.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
