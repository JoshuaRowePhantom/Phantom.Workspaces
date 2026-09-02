using ModelContextProtocol.Authentication;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Injection seam that supplies the host-provided pieces of the MCP SDK's OAuth client without the
/// transport factory taking a hard dependency on the interactive redirect handler (sub-item #1385)
/// or the persistent token cache (sub-item #1384). When no instance is threaded through
/// <see cref="Phantom.Workspaces.Llm.Interfaces.AgentServices.McpOAuthOptions"/>, the factory falls
/// back to <see cref="Default"/>: a redirect delegate that fails clearly in headless/unit contexts
/// and a null token cache (so the SDK uses its in-memory cache).
/// </summary>
/// <remarks>
/// <para>
/// Sub-item #1385 registers a real <see cref="RedirectDelegateProvider"/> (browser/loopback flow),
/// and sub-item #1384 registers a real <see cref="TokenCacheProvider"/> (persistent per-server
/// cache). Neither needs to touch the factory internals — they only replace the members here.
/// </para>
/// </remarks>
public sealed class McpOAuthOptions
{
    /// <summary>
    /// The safe default used when the host threads no OAuth options through <c>AgentServices</c>:
    /// the failing "interactive OAuth not configured" redirect delegate and a null token cache.
    /// </summary>
    public static McpOAuthOptions Default { get; } = new();

    /// <summary>
    /// Seam for sub-item #1385. Given the MCP server name, returns the SDK
    /// <see cref="AuthorizationRedirectDelegate"/> that drives the interactive authorization-code
    /// flow. When null, the factory uses <see cref="CreateNotConfiguredDelegate"/>.
    /// </summary>
    public Func<string, AuthorizationRedirectDelegate>? RedirectDelegateProvider { get; init; }

    /// <summary>
    /// Optional loopback/redirect URI handed to <c>ClientOAuthOptions.RedirectUri</c>. Left null by
    /// default — the real interactive handler (sub-item #1385) supplies a concrete listener URI. The
    /// factory never hardcodes a loopback listener here.
    /// </summary>
    public Uri? RedirectUri { get; init; }

    /// <summary>
    /// Seam for sub-item #1384. Given the MCP server name, returns an <see cref="ITokenCache"/> for
    /// that server, or null to let the SDK use its in-memory cache. When this provider itself is
    /// null, the factory leaves <c>ClientOAuthOptions.TokenCache</c> null.
    /// </summary>
    public Func<string, ITokenCache?>? TokenCacheProvider { get; init; }

    /// <summary>
    /// Resolves the authorization-redirect delegate for <paramref name="serverName"/>, defaulting to
    /// the failing "not configured" delegate when no provider is registered.
    /// </summary>
    public AuthorizationRedirectDelegate ResolveRedirectDelegate(string serverName)
        => this.RedirectDelegateProvider?.Invoke(serverName) ?? CreateNotConfiguredDelegate(serverName);

    /// <summary>
    /// Resolves the token cache for <paramref name="serverName"/>, or null when no provider is
    /// registered (SDK falls back to its <c>InMemoryTokenCache</c>).
    /// </summary>
    public ITokenCache? ResolveTokenCache(string serverName)
        => this.TokenCacheProvider?.Invoke(serverName);

    /// <summary>
    /// The default redirect delegate: it throws a clear, actionable error when the SDK tries to run
    /// an interactive authorization flow in a context where no interactive handler was registered.
    /// </summary>
    internal static AuthorizationRedirectDelegate CreateNotConfiguredDelegate(string serverName)
        => (_, _, _) => throw new InvalidOperationException(
            $"Interactive OAuth is not configured for MCP server '{serverName}'.");
}
