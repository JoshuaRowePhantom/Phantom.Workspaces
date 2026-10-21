using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelNamedAnonymousEndpointResolver(
    IAccessBroker broker,
    DevTunnelIdentityCredentialAccessRequest identityRequest,
    DevTunnelConnectCredentialAccessRequest relayRequest,
    Func<AccessPresentationScope> presentationScope,
    Func<HttpMessageHandler>? managementTransportFactory) : IDevTunnelEndpointResolver
{
    public async Task<DevTunnelEndpointResolution> ResolveAsync(
        string tunnelName,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accessMode != DevTunnelAccessMode.Anonymous ||
            relayRequest.Tunnel.Access != DevTunnelAccessKind.Anonymous ||
            !string.Equals(tunnelName, relayRequest.Tunnel.Name, StringComparison.Ordinal) ||
            identityRequest.Route is AnonymousDevTunnelIdentityRoute)
        {
            throw new DevTunnelCredentialAccessException(AccessErrorCode.IdentityMismatch);
        }

        Uri endpoint;
        using (var handler = new DevTunnelManagementAuthenticationHandler(
            broker,
            identityRequest,
            new ManagementResponseLifetime(
                managementTransportFactory?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false }),
            presentationScope))
        {
            await using var management = new TunnelManagementClient(
                new ProductInfoHeaderValue("Phantom.Workspaces", "1.0"),
                userTokenCallback: null,
                tunnelServiceUri: null,
                httpHandler: handler,
                apiVersion: ManagementApiVersions.Version20230927Preview);
            try
            {
                var candidates = await management.ListTunnelsAsync(null, null, new TunnelRequestOptions
                {
                    IncludePorts = true,
                    Labels = [DevTunnelNaming.WorkspacesMarkerLabel],
                    RequireAllLabels = true,
                }, true, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var matches = (candidates ?? []).Where(tunnel =>
                    tunnel.Labels?.Contains(DevTunnelNaming.WorkspacesMarkerLabel, StringComparer.Ordinal) == true &&
                    (DevTunnelNaming.IsAuto(tunnelName) ||
                        tunnel.Labels.Contains(tunnelName, StringComparer.Ordinal))).ToArray();
                if (matches.Length != 1 ||
                    matches[0].Ports is not { Length: 1 } ports ||
                    ports[0] is not { PortNumber: > 0 } ||
                    !DevTunnelConnectManagementApi.TryEndpoint(matches[0], out endpoint))
                {
                    throw new DevTunnelCredentialAccessException(AccessErrorCode.IdentityMismatch);
                }
            }
            catch (DevTunnelCredentialAccessException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new DevTunnelCredentialAccessException(AccessErrorCode.ProviderFailure);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new DevTunnelEndpointResolution(endpoint);
    }

    // The SDK does not dispose every ListTunnels response, including a discarded late response.
    private sealed class ManagementResponseLifetime(HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        private readonly object gate = new();
        private readonly List<HttpResponseMessage> responses = [];
        private bool disposed;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            lock (this.gate)
            {
                if (!this.disposed)
                {
                    this.responses.Add(response);
                    return response;
                }
            }
            response.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(ManagementResponseLifetime));
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }
            HttpResponseMessage[] owned;
            lock (this.gate)
            {
                if (this.disposed) { return; }
                this.disposed = true;
                owned = this.responses.ToArray();
                this.responses.Clear();
            }
            var failures = new List<Exception>();
            foreach (var response in owned)
            {
                try
                {
                    response.RequestMessage?.Dispose();
                    response.Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            try
            {
                base.Dispose(disposing);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            if (failures.Count > 0)
            {
                throw new AggregateException("Dev Tunnel discovery cleanup failed.", failures);
            }
        }
    }
}
