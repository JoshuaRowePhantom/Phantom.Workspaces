using AgentSchema;
using System.Net.Http;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Services.Mcp;

internal static class McpOAuthComposition
{
    internal static McpOAuthCredentialRequestCatalog CreateCatalog(
        Func<McpTool, McpOAuthProviderConfiguration?> configurationFactory) => new(configurationFactory);

    internal static McpOAuthCredentialRequestCatalog CreateCatalog(
        IPlatformSecretStore platformSecretStore,
        Func<HttpMessageHandler>? createHttpHandler = null) => new(platformSecretStore, createHttpHandler);

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
