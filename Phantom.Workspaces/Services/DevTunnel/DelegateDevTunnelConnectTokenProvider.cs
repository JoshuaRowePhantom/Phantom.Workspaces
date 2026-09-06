using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// A delegate-based <see cref="IDevTunnelConnectTokenProvider"/> that acquires the initial Connect token
/// from an <c>acquire</c> callback and, on a relay <c>401</c>, re-mints it from an optional
/// <c>refresh</c> callback (falling back to <c>acquire</c> when none is supplied). The most recently
/// acquired token is cached and returned by <see cref="GetConnectTokenAsync"/>.
/// </summary>
/// <remarks>
/// This is the Wave-2 default used to wire the existing sources without introducing Management-API
/// minting (Wave 3, #1458): the dev-tunnel source acquires the resolution's Connect token with no
/// refresh, and the GitHub-token web source acquires/refreshes via the GitHub auth-token resolver.
/// </remarks>
public sealed class DelegateDevTunnelConnectTokenProvider : IDevTunnelConnectTokenProvider
{
    private readonly Func<CancellationToken, ValueTask<string?>> acquire;
    private readonly Func<CancellationToken, ValueTask<string?>> refresh;
    private readonly object gate = new();
    private string? cachedToken;
    private bool hasCachedToken;

    public DelegateDevTunnelConnectTokenProvider(
        Func<CancellationToken, ValueTask<string?>> acquire,
        Func<CancellationToken, ValueTask<string?>>? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        this.acquire = acquire;
        this.refresh = refresh ?? acquire;
    }

    public async ValueTask<string?> GetConnectTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (this.gate)
        {
            if (this.hasCachedToken)
            {
                return this.cachedToken;
            }
        }

        var token = await this.acquire(cancellationToken).ConfigureAwait(false);
        lock (this.gate)
        {
            this.cachedToken = token;
            this.hasCachedToken = true;
            return this.cachedToken;
        }
    }

    public async ValueTask<string?> RefreshConnectTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (this.gate)
        {
            this.cachedToken = null;
            this.hasCachedToken = false;
        }

        var token = await this.refresh(cancellationToken).ConfigureAwait(false);
        lock (this.gate)
        {
            this.cachedToken = token;
            this.hasCachedToken = true;
            return this.cachedToken;
        }
    }
}
