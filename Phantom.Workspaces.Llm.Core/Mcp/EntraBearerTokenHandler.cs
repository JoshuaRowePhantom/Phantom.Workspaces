using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// A <see cref="DelegatingHandler"/> that attaches a Microsoft Entra bearer token to outbound MCP
/// requests, pinned to a single configured HTTPS origin (issue #1420). Before adding the
/// <c>Authorization: Bearer</c> header it requires the request URI to match the configured origin
/// (scheme + host + port) exactly; on any mismatch it attaches nothing, so a token is never leaked to
/// a different origin. Combined with an inner handler whose <c>AllowAutoRedirect</c> is disabled, a
/// cross-origin redirect can never carry the bearer.
/// </summary>
/// <remarks>
/// This is the only bespoke OAuth code in the <c>entra-pinned</c> path — the security policy
/// (origin-pinning + bearer attach), not the protocol. Token acquisition, PKCE, silent refresh, and
/// caching are delegated to <see cref="EntraPinnedTokenProvider"/> and the underlying credential
/// library. The pinned origin is derived from the configured MCP endpoint and is never taken from
/// remote metadata.
/// </remarks>
public sealed class EntraBearerTokenHandler : DelegatingHandler
{
    private readonly EntraPinnedTokenProvider tokenProvider;
    private readonly Uri allowedOrigin;
    private readonly ILogger logger;

    /// <summary>
    /// Creates a handler that attaches tokens from <paramref name="tokenProvider"/> only to requests
    /// whose origin matches <paramref name="allowedOrigin"/>.
    /// </summary>
    /// <param name="tokenProvider">Supplies the bearer token (single-flight, cached).</param>
    /// <param name="allowedOrigin">The exact HTTPS origin the bearer may be attached to.</param>
    /// <param name="logger">
    /// Optional logger. Records only whether the bearer was attached or stripped and for which origin,
    /// never the token value (#1446/#1408 redaction).
    /// </param>
    public EntraBearerTokenHandler(EntraPinnedTokenProvider tokenProvider, Uri allowedOrigin, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(allowedOrigin);

        this.tokenProvider = tokenProvider;
        this.allowedOrigin = allowedOrigin;
        this.logger = logger ?? NullLogger<EntraBearerTokenHandler>.Instance;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is { } target && this.IsAllowedOrigin(target))
        {
            var token = await this.tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            this.logger.LogDebug(
                "Attached host-pinned Entra bearer to request for origin {Origin}.",
                this.allowedOrigin.GetLeftPart(UriPartial.Authority));
        }
        else
        {
            // Defensively strip any authorization the caller (or a prior hop) may have set: this
            // handler pins the bearer to exactly one origin and never forwards it elsewhere.
            request.Headers.Authorization = null;
            this.logger.LogDebug(
                "Stripped authorization from a request that did not match the pinned origin {Origin}.",
                this.allowedOrigin.GetLeftPart(UriPartial.Authority));
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private bool IsAllowedOrigin(Uri target)
        => target.IsAbsoluteUri
            && string.Equals(target.Scheme, this.allowedOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Host, this.allowedOrigin.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == this.allowedOrigin.Port;
}
