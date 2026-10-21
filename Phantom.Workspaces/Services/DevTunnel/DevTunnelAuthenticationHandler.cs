using System.Net;
using System.Net.Http;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

public sealed class DevTunnelAuthenticationHandler : DelegatingHandler
{
    private const string TunnelAuthorizationHeader = "X-Tunnel-Authorization";
    private readonly IAccessBroker broker;
    private readonly DevTunnelConnectCredentialAccessRequest credentialRequest;
    private readonly Func<AccessPresentationScope>? presentationScope;

    public DevTunnelAuthenticationHandler(
        IAccessBroker broker,
        DevTunnelConnectCredentialAccessRequest credentialRequest,
        HttpMessageHandler innerHandler,
        Func<AccessPresentationScope>? presentationScope = null) : base(innerHandler)
    {
        this.broker = broker;
        this.credentialRequest = credentialRequest;
        this.presentationScope = presentationScope;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (this.credentialRequest.Tunnel.Endpoint is { } endpoint)
        {
            DevTunnelClientAuthorization.ValidateEndpoint(request.RequestUri, endpoint);
        }
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var credential = DevTunnelCredentialAccessException.Require(
            await this.broker.AcquireAsync(this.CurrentRequest(), cancellationToken).ConfigureAwait(false));
        using var outgoing = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await this.SendWithCredentialAsync(outgoing, credential, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || credential.ConnectToken is null)
        {
            return response;
        }
        response.Dispose();
        await credential.InvalidateRejectedGenerationAsync(cancellationToken).ConfigureAwait(false);

        // A missing Connect generation causes the broker to wait for a new explicit Perform.
        await using var renewed = DevTunnelCredentialAccessException.Require(
            await this.broker.AcquireAsync(this.CurrentRequest(), cancellationToken).ConfigureAwait(false));
        using var retry = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var replayResponse = await this.SendWithCredentialAsync(retry, renewed, cancellationToken).ConfigureAwait(false);
        if (replayResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            await renewed.InvalidateRejectedGenerationAsync(cancellationToken).ConfigureAwait(false);
        }
        return replayResponse;
    }

    private DevTunnelConnectCredentialAccessRequest CurrentRequest() =>
        this.presentationScope is null ? this.credentialRequest :
            this.credentialRequest with { PresentationScope = this.presentationScope() };

    private async Task<HttpResponseMessage> SendWithCredentialAsync(
        HttpRequestMessage request,
        DevTunnelConnectCredentialAccessResult credential,
        CancellationToken cancellationToken)
    {
        DevTunnelClientAuthorization.ValidateEndpoint(request.RequestUri, credential);
        request.Headers.Authorization = null;
        request.Headers.Remove(TunnelAuthorizationHeader);
        if (credential.ConnectToken is null)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            SanitizeResponseRequest(response, request);
            return response;
        }
        return DevTunnelCredentialAccessException.RequireUse(await credential.ConnectToken.UseAsync(
            async (material, token) =>
            {
                request.Headers.TryAddWithoutValidation(TunnelAuthorizationHeader, $"tunnel {material}");
                try
                {
                    var response = await base.SendAsync(request, token).ConfigureAwait(false);
                    SanitizeResponseRequest(response, request);
                    return response;
                }
                finally
                {
                    request.Headers.Remove(TunnelAuthorizationHeader);
                }
            }, cancellationToken).ConfigureAwait(false));
    }

    internal static void SanitizeResponseRequest(HttpResponseMessage response, HttpRequestMessage request)
    {
        // A transport may return a caller-owned request or an authenticated copy.
        response.RequestMessage = CloneRequestMetadata(response.RequestMessage ?? request);
    }

    internal static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = CloneRequestMetadata(request);
        try
        {
            if (request.Content is not null)
            {
                clone.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage CloneRequestMetadata(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        try
        {
            foreach (var header in request.Headers)
            {
                if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals(TunnelAuthorizationHeader, StringComparison.OrdinalIgnoreCase))
                {
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            foreach (var option in request.Options)
            {
                ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
            }
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }
}
