using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Token-provider seam for <see cref="DevTunnelAuthenticationHandler"/> (issue #1456). Supplies the
/// Connect-scope tunnel access token attached as <c>X-Tunnel-Authorization: tunnel {token}</c> on every
/// request to the <c>*.devtunnels.ms</c> relay, and re-mints it when the relay rejects the current token
/// with <c>401</c>.
/// </summary>
/// <remarks>
/// This is only the seam: the concrete Management-API Connect-token minting provider (interactive
/// sign-in, backoff, persistent reuse) lands in Wave 3 (#1458). Wave 2 ships this abstraction plus the
/// delegate-based <see cref="DelegateDevTunnelConnectTokenProvider"/> so the handler and both refactored
/// web clients can be wired and unit-tested against a test double.
/// </remarks>
public interface IDevTunnelConnectTokenProvider
{
    /// <summary>
    /// Returns the current Connect token to attach to an outgoing request, or <see langword="null"/> for
    /// anonymous access (no header). May return a cached value.
    /// </summary>
    ValueTask<string?> GetConnectTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the cached Connect token and mints a fresh one (interactively if required), returning
    /// the new value (or <see langword="null"/> if none can be obtained). Invoked by the handler after a
    /// relay <c>401</c>. The handler guarantees single-flight, so at most one call runs per refresh
    /// episode even under concurrent 401s.
    /// </summary>
    ValueTask<string?> RefreshConnectTokenAsync(CancellationToken cancellationToken = default);
}
