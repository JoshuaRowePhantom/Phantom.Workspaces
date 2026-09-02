using System.Net;
using System.Net.Sockets;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Mcp;

/// <summary>
/// Host composition root for the interactive MCP OAuth seam (sub-item #1385). Builds the
/// <see cref="McpOAuthOptions"/> bundle (redirect-delegate provider + concrete loopback redirect URI)
/// that the Avalonia host threads into <c>AgentServices.McpOAuthOptions</c> so the #1382 transport
/// factory uses the real interactive delegate. Only the GUI/desktop host calls this; headless hosts
/// leave the seam null and keep the failing default.
/// </summary>
public static class McpOAuthComposition
{
    public static McpOAuthOptions CreateOptions(ISecretProvider consentProvider)
        => CreateOptions(consentProvider, new SystemBrowserLauncher());

    public static McpOAuthOptions CreateOptions(
        ISecretProvider consentProvider,
        ISystemBrowserLauncher browserLauncher)
    {
        ArgumentNullException.ThrowIfNull(consentProvider);
        ArgumentNullException.ThrowIfNull(browserLauncher);

        var handler = new McpOAuthRedirectHandler(browserLauncher, consentProvider);
        return new McpOAuthOptions
        {
            RedirectDelegateProvider = handler.CreateRedirectDelegate,
            RedirectUri = CreateLoopbackRedirectUri(),
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
