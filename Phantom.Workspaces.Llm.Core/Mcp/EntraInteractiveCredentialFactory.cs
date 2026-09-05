using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Builds the default host-pinned Entra <see cref="TokenCredential"/> — an
/// <see cref="InteractiveBrowserCredential"/> — for the <c>entra-pinned</c> MCP OAuth mode (issue
/// #1420, integration points D and E). The GUI/desktop host wires this factory into
/// <see cref="McpOAuthOptions.EntraCredentialProvider"/>; headless/unit contexts inject a fake
/// credential instead, so the transport factory never takes a hard MSAL dependency.
/// </summary>
/// <remarks>
/// Tokens are persisted through MSAL's OS-backed cache (<see cref="TokenCachePersistenceOptions"/>)
/// keyed per MCP server, so a restart can silently refresh without a fresh interactive sign-in and
/// servers never share tokens. Tokens are never written to entity data or ordinary files.
/// </remarks>
public static class EntraInteractiveCredentialFactory
{
    /// <summary>
    /// Creates an <see cref="InteractiveBrowserCredential"/> for <paramref name="request"/>. The
    /// authority is split into its authority host and tenant id; the configured client id and host
    /// loopback redirect URI are applied when present.
    /// </summary>
    public static TokenCredential Create(McpEntraPinnedTokenRequest request)
        => Create(request, loggerFactory: null);

    /// <summary>
    /// Creates an <see cref="InteractiveBrowserCredential"/> for <paramref name="request"/>, logging the
    /// credential creation through <paramref name="loggerFactory"/> when supplied. Only the server name
    /// and (safe) authority host are logged — never a client secret or token (#1446/#1408 redaction).
    /// </summary>
    public static TokenCredential Create(McpEntraPinnedTokenRequest request, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(request);

        var logger = loggerFactory?.CreateLogger("Phantom.Workspaces.Llm.Mcp.EntraInteractiveCredentialFactory");
        if (TryParseAuthority(request.Authority, out var authorityHost, out _))
        {
            logger?.LogInformation(
                "Creating host-pinned Entra credential for MCP server '{ServerName}' against authority host {AuthorityHost}.",
                request.ServerName,
                authorityHost.GetLeftPart(UriPartial.Authority));
        }
        else
        {
            logger?.LogInformation(
                "Creating host-pinned Entra credential for MCP server '{ServerName}'.", request.ServerName);
        }

        return new InteractiveBrowserCredential(BuildOptions(request));
    }

    /// <summary>
    /// Builds the <see cref="InteractiveBrowserCredentialOptions"/> for <paramref name="request"/>. The
    /// authority is split into its authority host and tenant id; the configured client id and (only when
    /// present) redirect URI are applied. When <see cref="McpEntraPinnedTokenRequest.RedirectUri"/> is
    /// null the redirect URI is left unset so MSAL binds its own ephemeral loopback listener (#1427).
    /// </summary>
    internal static InteractiveBrowserCredentialOptions BuildOptions(McpEntraPinnedTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "phantom-mcp-oauth:" + request.ServerName,
            },
        };

        if (TryParseAuthority(request.Authority, out var authorityHost, out var tenantId))
        {
            options.AuthorityHost = authorityHost;
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                options.TenantId = tenantId;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            options.ClientId = request.ClientId;
        }

        if (request.RedirectUri is { } redirectUri)
        {
            options.RedirectUri = redirectUri;
        }

        return options;
    }

    /// <summary>
    /// Splits an Entra authority such as <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>
    /// into its authority host (<c>https://login.microsoftonline.com/</c>) and tenant id
    /// (<c>&lt;tenant&gt;</c>). Returns false when <paramref name="authority"/> is not an absolute URI.
    /// </summary>
    internal static bool TryParseAuthority(string? authority, out Uri authorityHost, out string? tenantId)
    {
        authorityHost = default!;
        tenantId = null;

        if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return false;
        }

        authorityHost = new Uri(uri.GetLeftPart(UriPartial.Authority));

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && !string.Equals(segments[0], "v2.0", StringComparison.OrdinalIgnoreCase))
        {
            tenantId = segments[0];
        }

        return true;
    }
}
