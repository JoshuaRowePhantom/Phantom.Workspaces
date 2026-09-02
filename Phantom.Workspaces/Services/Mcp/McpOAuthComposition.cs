using System.Net;
using System.Net.Sockets;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Mcp;

/// <summary>
/// Host composition root for the MCP OAuth seams. Builds the <see cref="McpOAuthOptions"/> bundle
/// that the Avalonia host threads into <c>AgentServices.McpOAuthOptions</c> so the #1382 transport
/// factory uses the real host-provided pieces: the interactive redirect-delegate provider + concrete
/// loopback redirect URI (sub-item #1385) AND the persistent per-server token-cache provider
/// (sub-item #1384). Only the GUI/desktop host calls this; headless hosts leave the seam null and
/// keep the failing default.
/// </summary>
public static class McpOAuthComposition
{
    public static McpOAuthOptions CreateOptions(ISecretProvider consentProvider)
        => CreateOptions(consentProvider, secretStore: null);

    public static McpOAuthOptions CreateOptions(
        ISecretProvider consentProvider,
        IPlatformSecretStore? secretStore)
        => CreateOptions(consentProvider, new SystemBrowserLauncher(), secretStore);

    public static McpOAuthOptions CreateOptions(
        ISecretProvider consentProvider,
        ISystemBrowserLauncher browserLauncher,
        IPlatformSecretStore? secretStore = null)
    {
        ArgumentNullException.ThrowIfNull(consentProvider);
        ArgumentNullException.ThrowIfNull(browserLauncher);

        var handler = new McpOAuthRedirectHandler(browserLauncher, consentProvider);
        return new McpOAuthOptions
        {
            // #1385: interactive redirect delegate + loopback listener URI.
            RedirectDelegateProvider = handler.CreateRedirectDelegate,
            RedirectUri = CreateLoopbackRedirectUri(),

            // #1384: persistent per-server token cache over the platform secret store. Returns null
            // per server when no real store is available (non-Windows) so the SDK in-memory cache is
            // used.
            TokenCacheProvider = CredentialManagerTokenCache.CreateProvider(secretStore),
        };
    }

    /// <summary>
    /// Reserves a free loopback TCP port and returns the <c>http://127.0.0.1:&lt;port&gt;/</c> redirect
    /// URI. The handler binds its <see cref="HttpListener"/> to the same port when the SDK invokes the
    /// delegate.
    /// </summary>
    internal static Uri CreateLoopbackRedirectUri()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new Uri($"http://127.0.0.1:{port}/");
        }
        finally
        {
            listener.Stop();
        }
    }
}
