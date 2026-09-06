using System;
using System.Net.Http;

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
        this.innerHandlerFactory = innerHandlerFactory ?? (() => new HttpClientHandler());
    }

    public HttpClient CreateClient(Uri baseAddress, IDevTunnelConnectTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var handler = new DevTunnelAuthenticationHandler(tokenProvider, this.innerHandlerFactory());
        return new HttpClient(handler)
        {
            BaseAddress = baseAddress,
        };
    }
}
