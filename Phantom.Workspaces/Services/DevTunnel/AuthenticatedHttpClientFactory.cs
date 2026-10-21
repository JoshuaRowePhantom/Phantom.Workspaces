using System;
using System.Net.Http;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Default <see cref="IAuthenticatedHttpClientFactory"/>: wraps a fresh transport handler in a
/// <see cref="DevTunnelAuthenticationHandler"/> and returns an <see cref="HttpClient"/> bound to the
/// supplied base address. An optional inner-handler factory lets tests substitute a recording transport.
/// </summary>
public sealed class AuthenticatedHttpClientFactory : IAuthenticatedHttpClientFactory
{
    private readonly Func<HttpMessageHandler> innerHandlerFactory;

    public AuthenticatedHttpClientFactory(Func<HttpMessageHandler>? innerHandlerFactory = null)
    {
        this.innerHandlerFactory = innerHandlerFactory ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
    }

    public HttpClient CreateClient(Uri baseAddress, IAccessBroker broker, DevTunnelConnectCredentialAccessRequest request,
        Func<AccessPresentationScope>? presentationScope = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(request);

        var handler = new DevTunnelAuthenticationHandler(broker, request, this.innerHandlerFactory(), presentationScope);
        return new HttpClient(handler)
        {
            BaseAddress = baseAddress,
        };
    }
}
