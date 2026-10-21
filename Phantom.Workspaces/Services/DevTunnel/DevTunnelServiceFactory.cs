using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

public sealed class DevTunnelServiceFactory(
    IAccessBroker broker,
    DevTunnelIdentityCredentialAccessRequest identityRequest,
    Func<AccessPresentationScope> presentationScope)
{
    private readonly Func<AccessPresentationScope> getPresentationScope =
        presentationScope ?? throw new ArgumentNullException(nameof(presentationScope));

    internal Func<HttpMessageHandler>? ManagementTransportFactory { get; init; }

    public IDevTunnelHostService CreateHostService()
    {
        var handler = new DevTunnelManagementAuthenticationHandler(
            broker, identityRequest, new HttpClientHandler { AllowAutoRedirect = false }, this.getPresentationScope);
        var management = new TunnelManagementClient(
            new ProductInfoHeaderValue("Phantom.Workspaces", "1.0"),
            userTokenCallback: null,
            tunnelServiceUri: null,
            httpHandler: handler,
            apiVersion: ManagementApiVersions.Version20230927Preview);
        var wrapper = new DevTunnelManagementClientWrapper(management);
        return new DevTunnelHostService(wrapper, new TunnelRelayDevTunnelHost(wrapper),
            new ManagementLifetime(management, handler));
    }

    public IDevTunnelEndpointResolver CreateEndpointResolver(DevTunnelConnectCredentialAccessRequest request) =>
        request.Tunnel.Access == DevTunnelAccessKind.Anonymous && request.Tunnel.Endpoint is null
            ? new DevTunnelNamedAnonymousEndpointResolver(
                broker, identityRequest, request, this.getPresentationScope, this.ManagementTransportFactory)
            : new DevTunnelEndpointResolver(broker, request, this.getPresentationScope);

    private sealed class ManagementLifetime(TunnelManagementClient management, HttpMessageHandler handler) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }
            try
            {
                await management.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                handler.Dispose();
            }
        }
    }
}
