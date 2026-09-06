using Azure.Core;
using ModelContextProtocol.Authentication;

namespace Phantom.Workspaces.Llm.Auth;

/// <summary>
/// Describes a single host-pinned Entra credential acquisition (issue #1420). Passed to the
/// <see cref="InteractiveOAuthOptions.EntraCredentialProvider"/> seam so the host can build a
/// <see cref="TokenCredential"/> (normally an <c>InteractiveBrowserCredential</c>) for the statically
/// configured authority/client, and unit contexts can inject a fake credential without an MSAL
/// dependency. This is a neutral, auth-generic type (issue #1454): the caller (MCP today, dev-tunnel
/// later) supplies its own <see cref="CallerName"/>.
/// </summary>
/// <param name="Authority">The Entra tenant authority (e.g. <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>).</param>
/// <param name="ClientId">The configured OAuth client id, or null for the credential's default.</param>
/// <param name="RedirectUri">The host loopback redirect URI, or null when none was supplied.</param>
/// <param name="CallerName">The caller's display name (e.g. the MCP server name); used to key the token cache per caller.</param>
public sealed record EntraPinnedTokenRequest(
    string Authority,
    string? ClientId,
    Uri? RedirectUri,
    string CallerName);


/// <summary>
/// Injection seam that supplies the host-provided pieces of an interactive OAuth client (issue #1454)
/// without the transport factory taking a hard dependency on the interactive redirect handler
/// (sub-item #1385) or the persistent token cache (sub-item #1384). This is the neutral, auth-generic
/// surface shared by MCP (today) and dev-tunnel (later). When no instance is threaded through
/// <see cref="Phantom.Workspaces.Llm.Interfaces.AgentServices.McpOAuthOptions"/>, the factory falls
/// back to <see cref="Default"/>: a redirect delegate that fails clearly in headless/unit contexts
/// and a null token cache (so the SDK uses its in-memory cache).
/// </summary>
/// <remarks>
/// <para>
/// Sub-item #1385 registers a real <see cref="RedirectDelegateProvider"/> (browser/loopback flow),
/// and sub-item #1384 registers a real <see cref="TokenCacheProvider"/> (persistent per-caller
/// cache). Neither needs to touch the factory internals — they only replace the members here.
/// </para>
/// </remarks>
public sealed class InteractiveOAuthOptions
{
    /// <summary>
    /// The safe default used when the host threads no OAuth options through <c>AgentServices</c>:
    /// the failing "interactive OAuth not configured" redirect delegate and a null token cache.
    /// </summary>
    public static InteractiveOAuthOptions Default { get; } = new();

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
    /// Seam for the host-pinned Entra mode (issue #1420, integration point D). Given a
    /// <see cref="EntraPinnedTokenRequest"/> (authority/client id/redirect URI/caller name), returns
    /// the <see cref="TokenCredential"/> used to acquire access tokens for the statically configured
    /// authority — normally an <c>InteractiveBrowserCredential</c>. When null, the factory throws a
    /// clear "not configured" error for any <c>entra-pinned</c> connection (headless/unit contexts that
    /// do not inject a credential). This mirrors <see cref="RedirectDelegateProvider"/> /
    /// <see cref="TokenCacheProvider"/>, keeping the transport factory free of a hard MSAL dependency.
    /// </summary>
    public Func<EntraPinnedTokenRequest, TokenCredential>? EntraCredentialProvider { get; init; }

    /// <summary>
    /// Resolves the host-pinned Entra <see cref="TokenCredential"/> for <paramref name="request"/>, or
    /// throws a clear, actionable error when no provider is registered.
    /// </summary>
    public TokenCredential ResolveEntraCredential(EntraPinnedTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.EntraCredentialProvider?.Invoke(request)
            ?? throw new InvalidOperationException(
                $"Host-pinned Entra authentication is not configured for '{request.CallerName}'.");
    }

    /// <summary>
    /// The default redirect delegate: it throws a clear, actionable error when the SDK tries to run
    /// an interactive authorization flow in a context where no interactive handler was registered.
    /// </summary>
    internal static AuthorizationRedirectDelegate CreateNotConfiguredDelegate(string serverName)
        => (_, _, _) => throw new InvalidOperationException(
            $"Interactive OAuth is not configured for MCP server '{serverName}'.");
}
