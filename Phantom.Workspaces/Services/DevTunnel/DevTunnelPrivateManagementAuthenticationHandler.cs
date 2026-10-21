using System.Net.Http;
using System.Net.Http.Headers;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelPrivateManagementAuthenticationHandler(
    AuthenticationHeaderValue identityHeader,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private AuthenticationHeaderValue? identity = identityHeader;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        DevTunnelManagementAuthenticationHandler.ValidateDestination(request.RequestUri);
        var header = Volatile.Read(ref this.identity) ??
            throw new ObjectDisposedException(nameof(DevTunnelPrivateManagementAuthenticationHandler));
        using var outgoing = await DevTunnelAuthenticationHandler.CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        outgoing.Headers.Authorization = header;
        try
        {
            var response = await base.SendAsync(outgoing, cancellationToken).ConfigureAwait(false);
            DevTunnelAuthenticationHandler.SanitizeResponseRequest(response, outgoing);
            return response;
        }
        finally
        {
            outgoing.Headers.Authorization = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref this.identity, null);
        }
        base.Dispose(disposing);
    }
}
