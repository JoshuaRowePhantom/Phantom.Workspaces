using System;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Signals that a Dev Tunnels <b>Management-API</b> call was rejected because the caller's identity token
/// is expired or invalid (issue #1458). This is distinct from a relay <c>401</c> (handled by
/// <see cref="DevTunnelAuthenticationHandler"/>): it drives an <em>identity</em> refresh via the scheme's
/// <see cref="IDevTunnelAuthTokenProvider"/> before the Connect token is re-minted. Network / 5xx failures
/// must not be surfaced as this type so they never trigger re-authentication.
/// </summary>
public sealed class DevTunnelManagementUnauthorizedException : Exception
{
    public DevTunnelManagementUnauthorizedException()
        : base("The Dev Tunnels Management API rejected the current identity token as unauthorized.")
    {
    }

    public DevTunnelManagementUnauthorizedException(string message)
        : base(message)
    {
    }

    public DevTunnelManagementUnauthorizedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
