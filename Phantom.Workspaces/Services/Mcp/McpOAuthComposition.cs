using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Services.Mcp;

internal static class McpOAuthComposition
{
    internal static McpOAuthCredentialRequestCatalog CreateCatalog() => new();

    internal static McpOAuthCredentialAccessExecutor RegisterExecutor(
        CredentialAccessExecutorRegistry executors,
        McpOAuthCredentialRequestCatalog catalog,
        ISystemBrowserLauncher browserLauncher)
    {
        var executor = new McpOAuthCredentialAccessExecutor(catalog, browserLauncher);
        executors.Add<McpOAuthCredentialUseRequest, McpCredentialUseResult>(executor);
        return executor;
    }
}
