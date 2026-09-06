using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// A <see cref="DelegatingHandler"/> that authenticates dev-tunnel client requests to the
/// <c>*.devtunnels.ms</c> relay (issue #1456). It centralizes the previously hand-rolled, duplicated
/// header-setting and one-shot 401 retry that lived inside <c>WebClientDataAccessLayer</c> and
/// <c>WebClientAgentPersistenceStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per request it asks the <see cref="IDevTunnelConnectTokenProvider"/> for the current Connect token
/// and attaches <c>X-Tunnel-Authorization: tunnel {token}</c> to that request only — never mutating
/// <see cref="HttpClient.DefaultRequestHeaders"/>. Anonymous access (a <see langword="null"/> token)
/// sends no header.
/// </para>
/// <para>
/// On a relay <c>401 Unauthorized</c> it invalidates the cached token, asks the provider to re-mint, and
/// retries the request exactly <b>once</b> with a buffered clone. Concurrent 401s that share the same
/// stale token coalesce into a <b>single</b> refresh episode (a lock-guarded shared <see cref="Task"/>
/// keyed on the stale token), so the provider re-mints — and prompts interactively — at most once per
/// episode. Only auth failures drive refresh: a network error propagates and a non-401 response (e.g.
/// 5xx) is returned verbatim without re-authenticating.
/// </para>
/// <para>
/// This handler owns <b>auth</b> (401). Endpoint <b>drops</b> (transport failures) are handled one layer
/// out by <c>ReconnectingWebDataAccessLayer</c> / <c>ReconnectingWebAgentPersistenceStore</c>, which
/// re-resolve the tunnel and rebuild the authenticated client.
/// </para>
/// </remarks>
public sealed class DevTunnelAuthenticationHandler : DelegatingHandler
{
    private const string TunnelAuthorizationHeader = "X-Tunnel-Authorization";

    private readonly IDevTunnelConnectTokenProvider tokenProvider;
    private readonly object refreshGate = new();
    private Task<string?>? refreshEpisode;
    private string? refreshEpisodeStaleToken;
    private bool refreshEpisodeActive;

    public DevTunnelAuthenticationHandler(IDevTunnelConnectTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        this.tokenProvider = tokenProvider;
    }

    public DevTunnelAuthenticationHandler(IDevTunnelConnectTokenProvider tokenProvider, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        this.tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await this.tokenProvider.GetConnectTokenAsync(cancellationToken).ConfigureAwait(false);
        ApplyTunnelAuthorization(request, token);

        // Buffer the content so the request can be replayed if the relay rejects the current token.
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            // Non-auth failures (5xx / other) are returned verbatim; only 401 drives re-authentication.
            return response;
        }

        response.Dispose();

        // Single-flight: concurrent 401s carrying the same stale token share one re-mint episode.
        var freshToken = await this.RefreshCoalescedAsync(token, cancellationToken).ConfigureAwait(false);

        var retry = await CloneRequestAsync(request).ConfigureAwait(false);
        ApplyTunnelAuthorization(retry, freshToken);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private Task<string?> RefreshCoalescedAsync(string? staleToken, CancellationToken cancellationToken)
    {
        lock (this.refreshGate)
        {
            // Reuse an episode started from the same stale token — whether it is still in flight or has
            // already completed — so late-arriving 401s never trigger a second re-mint for that token.
            if (this.refreshEpisodeActive
                && this.refreshEpisode is not null
                && string.Equals(this.refreshEpisodeStaleToken, staleToken, StringComparison.Ordinal))
            {
                return this.refreshEpisode;
            }

            this.refreshEpisodeActive = true;
            this.refreshEpisodeStaleToken = staleToken;
            this.refreshEpisode = this.tokenProvider.RefreshConnectTokenAsync(cancellationToken).AsTask();
            return this.refreshEpisode;
        }
    }

    private static void ApplyTunnelAuthorization(HttpRequestMessage request, string? token)
    {
        request.Headers.Remove(TunnelAuthorizationHeader);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation(TunnelAuthorizationHeader, $"tunnel {token}");
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (KeyValuePair<string, object?> option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }
}
