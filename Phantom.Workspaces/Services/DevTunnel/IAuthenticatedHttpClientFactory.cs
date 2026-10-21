using System;
using System.Net.Http;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Builds an already-authenticated <see cref="HttpClient"/> for a dev-tunnel / web source (issue #1456).
/// The returned client has a <see cref="DevTunnelAuthenticationHandler"/> at the top of its pipeline, so
/// every request carries the Connect token and transparently re-authenticates on a relay <c>401</c>.
/// Consuming clients (<c>WebClientDataAccessLayer</c>, <c>WebClientAgentPersistenceStore</c>) receive a
/// finished client and no longer know anything about tunnels or tokens.
/// </summary>
public interface IAuthenticatedHttpClientFactory
{
    /// <summary>
    /// Creates an authenticated <see cref="HttpClient"/> bound to <paramref name="baseAddress"/> that
    /// exercises only a broker-owned Connect lease for <paramref name="request"/>.
    /// The required live <paramref name="presentationScope"/> is evaluated before every acquisition.
    /// </summary>
    HttpClient CreateClient(Uri baseAddress, IAccessBroker broker, DevTunnelConnectCredentialAccessRequest request,
        Func<AccessPresentationScope> presentationScope);
}
