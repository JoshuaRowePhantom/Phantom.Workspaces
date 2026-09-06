using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Mcp;

/// <summary>
/// Host composition root for the MCP OAuth seams. Builds the <see cref="InteractiveOAuthOptions"/>
/// bundle that the Avalonia host threads into <c>AgentServices.McpOAuthOptions</c> so the #1382
/// transport factory uses the real host-provided pieces: the interactive redirect-delegate provider +
/// concrete loopback redirect URI (sub-item #1385) AND the persistent per-server token-cache provider
/// (sub-item #1384). Only the GUI/desktop host calls this; headless hosts leave the seam null and
/// keep the failing default.
/// </summary>
public static class McpOAuthComposition
{
    /// <summary>
    /// The caller-supplied token-cache key prefix for the MCP OAuth path (issue #1454). Dev-tunnel
    /// passes its own prefix; MCP keeps <c>mcp-oauth:</c> so the persisted key is unchanged.
    /// </summary>
    private const string McpTokenCacheKeyPrefix = "mcp-oauth:";

    public static InteractiveOAuthOptions CreateOptions(ISecretProvider consentProvider)
        => CreateOptions(consentProvider, secretStore: null);

    public static InteractiveOAuthOptions CreateOptions(
        ISecretProvider consentProvider,
        IPlatformSecretStore? secretStore,
        ILoggerFactory? loggerFactory = null)
        => CreateOptions(consentProvider, new SystemBrowserLauncher(), secretStore, loggerFactory);

    public static InteractiveOAuthOptions CreateOptions(
        ISecretProvider consentProvider,
        ISystemBrowserLauncher browserLauncher,
        IPlatformSecretStore? secretStore = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(consentProvider);
        ArgumentNullException.ThrowIfNull(browserLauncher);

        var handler = new McpOAuthRedirectHandler(
            browserLauncher,
            consentProvider,
            loggerFactory?.CreateLogger<McpOAuthRedirectHandler>());

        // #1425: bind the single shared loopback listener now and derive the redirect URI from its
        // actual bound prefix. The port is held continuously by the handler for the process lifetime
        // (no reserve-then-free TOCTOU), and every server reuses this one URI + listener.
        var redirectUri = handler.EnsureListenerBound();
        return new InteractiveOAuthOptions
        {
            // #1385: interactive redirect delegate + loopback listener URI.
            RedirectDelegateProvider = handler.CreateRedirectDelegate,
            RedirectUri = redirectUri,

            // #1384: persistent per-server token cache over the platform secret store. Returns null
            // per server when no real store is available (non-Windows) so the SDK in-memory cache is
            // used. #1454: the MCP key prefix is supplied here rather than hard-coded in the cache.
            TokenCacheProvider = CredentialManagerTokenCache.CreateProvider(secretStore, McpTokenCacheKeyPrefix, loggerFactory),

            // #1420 (integration point D): host-pinned Entra credential builder. It does NOT reuse the
            // shared DCR loopback redirect URI above (#1427) — MSAL's InteractiveBrowserCredential binds
            // its own ephemeral localhost loopback listener, which Entra matches port-agnostically, so the
            // two subsystems never contend for one port. Tokens persist through MSAL's OS-backed cache
            // keyed per server.
            EntraCredentialProvider = request => EntraInteractiveCredentialFactory.Create(request, loggerFactory),
        };
    }
}
