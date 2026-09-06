using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The <c>anonymous</c> dev-tunnel auth scheme (issue #1458): no identity is attached at the Management
/// API and the client relies on the tunnel's Anonymous access. <see cref="AuthenticationScheme"/> is
/// <see langword="null"/> so <see cref="DevTunnelServiceFactory"/> sends no Management-API auth header,
/// and <see cref="GetAccessTokenAsync"/> yields an empty token.
/// </summary>
public sealed class AnonymousDevTunnelAuthProvider : IDevTunnelAuthTokenProvider
{
    /// <inheritdoc />
    public string? AuthenticationScheme => null;

    /// <inheritdoc />
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);
}
