using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using Microsoft.DevTunnels.Contracts;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelManagementAuthenticationHandler(
    IAccessBroker broker,
    DevTunnelIdentityCredentialAccessRequest identityRequest,
    HttpMessageHandler innerHandler,
    Func<AccessPresentationScope> presentationScope) : DelegatingHandler(innerHandler)
{
    private readonly Func<AccessPresentationScope> getPresentationScope =
        presentationScope ?? throw new ArgumentNullException(nameof(presentationScope));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ValidateDestination(request.RequestUri);
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var identity = await DevTunnelCredentialAccessException.RequireAsync(
            await broker.AcquireAsync(CurrentRequest(), cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        using var outgoing = await DevTunnelAuthenticationHandler.CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await this.SendWithIdentityAsync(outgoing, identity, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || identity.AccessToken is null)
        {
            return response;
        }
        response.Dispose();
        await identity.InvalidateRejectedGenerationAsync(cancellationToken).ConfigureAwait(false);
        await using var renewed = await DevTunnelCredentialAccessException.RequireAsync(
            await broker.AcquireAsync(CurrentRequest(), cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        using var retry = await DevTunnelAuthenticationHandler.CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var replay = await this.SendWithIdentityAsync(retry, renewed, cancellationToken).ConfigureAwait(false);
        try
        {
            if (replay.StatusCode == HttpStatusCode.Unauthorized)
            {
                await renewed.InvalidateRejectedGenerationAsync(cancellationToken).ConfigureAwait(false);
            }
            return replay;
        }
        catch
        {
            replay.Dispose();
            throw;
        }
    }

    internal static void ValidateDestination(Uri? destination)
    {
        if (destination is not { IsAbsoluteUri: true } uri ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("global.rel.tunnels.api.visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".rel.tunnels.api.visualstudio.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DevTunnelCredentialAccessException(AccessErrorCode.IdentityMismatch);
        }
    }

    private DevTunnelIdentityCredentialAccessRequest CurrentRequest() =>
        identityRequest with { PresentationScope = this.getPresentationScope() };

    private async Task<HttpResponseMessage> SendWithIdentityAsync(
        HttpRequestMessage request,
        DevTunnelIdentityCredentialAccessResult identity,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;
        request.Headers.Remove("X-Tunnel-Authorization");
        if (identity.Scheme == DevTunnelIdentityScheme.Anonymous)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            DevTunnelAuthenticationHandler.SanitizeResponseRequest(response, request);
            return response;
        }
        if (identity.AccessToken is null)
        {
            throw new DevTunnelCredentialAccessException(AccessErrorCode.ProviderFailure);
        }
        return DevTunnelCredentialAccessException.RequireUse(await identity.AccessToken.UseAsync(
            async (material, token) =>
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(identity.Scheme switch
                {
                    DevTunnelIdentityScheme.GitHub => TunnelAuthenticationSchemes.GitHub,
                    DevTunnelIdentityScheme.Entra => TunnelAuthenticationSchemes.Aad,
                    _ => "Bearer",
                }, material);
                try
                {
                    var response = await base.SendAsync(request, token).ConfigureAwait(false);
                    DevTunnelAuthenticationHandler.SanitizeResponseRequest(response, request);
                    return response;
                }
                finally
                {
                    request.Headers.Authorization = null;
                }
            }, cancellationToken).ConfigureAwait(false));
    }
}
