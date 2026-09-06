using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces;

/// <summary>
/// Pluggable authentication configuration for a remote data/persistence source (dev-tunnel auth,
/// issue #1455). Replaces the hard-coded, GitHub-only <c>useGitHubAuthToken</c> flag with a reusable
/// scheme (<c>github</c> | <c>entra</c> | <c>oauth</c> | <c>anonymous</c>). Because both
/// <see cref="EntityRepository"/> and <c>AgentPersistenceStoreSourceFactory</c> pattern-match the same
/// <see cref="RepositorySource"/> hierarchy, attaching this single field to a source configures the
/// data DAL and the chat-persistence store uniformly.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>data-model only</b> type: the record, its JSON round-trip in <c>config.json</c>, the
/// <see cref="Normalize"/> mapping onto the external AgentSchema discriminators, and the
/// <see cref="ToPhantomOAuthConnection"/> / <see cref="FromPhantomOAuthConnection"/> adapter. Runtime
/// consumption (actually acquiring/attaching tokens) lives in sibling sub-items #1456/#1458.
/// </para>
/// <para>
/// The field names mirror <see cref="PhantomOAuthConnection"/> so a single adapter maps between them.
/// The <c>scheme</c> string is the discriminator (kept as a plain property rather than System.Text.Json
/// polymorphism so a new scheme value needs no new derived type and the record stays a single closed
/// shape that round-trips losslessly).
/// </para>
/// <para>
/// <see cref="ClientSecret"/> is only ever a <c>${SECRET:...}</c> / <c>${ENV}</c> placeholder, never a
/// raw secret — the persistence layer stores the placeholder verbatim and never materializes it.
/// </para>
/// <para>
/// Persisted as a camelCase <c>authentication</c> object in <c>config.json</c>, e.g.:
/// <code>
/// "authentication": {
///   "scheme": "entra",
///   "tenantId": "00000000-0000-0000-0000-000000000000",
///   "clientId": "11111111-1111-1111-1111-111111111111",
///   "scopes": ["api://example/.default"],
///   "remember": true
/// }
/// </code>
/// </para>
/// </remarks>
/// <param name="Scheme">The authentication scheme: <c>github</c>, <c>entra</c>, <c>oauth</c>, or <c>anonymous</c>.</param>
/// <param name="Endpoint">Optional endpoint the credential is pinned to (== <see cref="PhantomOAuthConnection.Endpoint"/>).</param>
/// <param name="Authority">Optional Entra authority (entra scheme).</param>
/// <param name="TenantId">Optional Entra tenant id (parsed from <see cref="Authority"/> or set explicitly).</param>
/// <param name="ClientId">Optional OAuth client id.</param>
/// <param name="ClientSecret">Optional client-secret placeholder (<c>${SECRET:...}</c> / <c>${ENV}</c>), never raw.</param>
/// <param name="AuthorizationEndpoint">Optional explicit authorization endpoint override (dev-tunnel-only; not forwarded to MCP metadata discovery).</param>
/// <param name="TokenEndpoint">Optional token endpoint (== <c>OAuthConnection.TokenUrl</c>).</param>
/// <param name="Scopes">Optional OAuth scopes.</param>
/// <param name="Remember">Whether to persist and silently reuse the acquired token. Off by default.</param>
/// <param name="TokenCacheKey">Optional token-cache key (e.g. <c>devtunnel:&lt;name&gt;</c>).</param>
public sealed record RemoteAuthentication(
    string Scheme,
    string? Endpoint = null,
    string? Authority = null,
    string? TenantId = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    IReadOnlyList<string>? Scopes = null,
    bool Remember = false,
    string? TokenCacheKey = null)
{
    /// <summary>The <c>github</c> scheme: GitHub token auth (mirrors the legacy <c>useGitHubAuthToken</c>).</summary>
    public const string GithubScheme = "github";

    /// <summary>The <c>entra</c> scheme: host-pinned Microsoft Entra auth.</summary>
    public const string EntraScheme = "entra";

    /// <summary>The <c>oauth</c> scheme: standard interactive OAuth via the SDK's system provider.</summary>
    public const string OAuthScheme = "oauth";

    /// <summary>The <c>anonymous</c> scheme: no credential attached.</summary>
    public const string AnonymousScheme = "anonymous";

    /// <summary>The AgentSchema <c>OAuthConnection</c> connection kind.</summary>
    private const string OAuthKind = "oauth";

    /// <summary>The AgentSchema <c>ApiKeyConnection</c> connection kind (key/api-key).</summary>
    private const string KeyKind = "key";

    /// <summary>The AgentSchema <c>AnonymousConnection</c> connection kind.</summary>
    private const string AnonymousKind = "anonymous";

    /// <summary>The default (system) OAuth authentication mode.</summary>
    private const string SystemAuthenticationMode = "system";

    /// <summary>
    /// Normalizes <see cref="Scheme"/> onto the external AgentSchema discriminators
    /// <c>(Kind, AuthenticationMode)</c>: <c>entra → (oauth, entra-pinned)</c>,
    /// <c>oauth → (oauth, system)</c>, <c>github → (key, null)</c>, <c>anonymous → (anonymous, null)</c>.
    /// </summary>
    public (string Kind, string? AuthenticationMode) Normalize() => NormalizeScheme(this.Scheme);

    /// <summary>
    /// Maps an authentication <paramref name="scheme"/> onto the external AgentSchema discriminators.
    /// </summary>
    public static (string Kind, string? AuthenticationMode) NormalizeScheme(string scheme)
        => (scheme ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            EntraScheme => (OAuthKind, PhantomAgentSchema.EntraPinnedAuthenticationMode),
            OAuthScheme => (OAuthKind, SystemAuthenticationMode),
            GithubScheme => (KeyKind, null),
            AnonymousScheme => (AnonymousKind, null),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scheme), scheme, "Unknown RemoteAuthentication scheme."),
        };

    /// <summary>
    /// Projects this configuration into a <see cref="PhantomOAuthConnection"/>, round-tripping the shared
    /// fields (<c>Endpoint</c>, <c>ClientId</c>, <c>ClientSecret</c>, <c>TokenUrl</c>↔<see cref="TokenEndpoint"/>,
    /// <c>Scopes</c>, <c>Authority</c>). <see cref="AuthorizationEndpoint"/> is dev-tunnel-only and is
    /// intentionally not forwarded (MCP metadata discovery derives it instead).
    /// </summary>
    public PhantomOAuthConnection ToPhantomOAuthConnection()
    {
        var (kind, authenticationMode) = this.Normalize();
        return new PhantomOAuthConnection
        {
            Kind = kind,
            AuthenticationMode = authenticationMode!,
            Endpoint = this.Endpoint!,
            ClientId = this.ClientId!,
            ClientSecret = this.ClientSecret!,
            TokenUrl = this.TokenEndpoint!,
            Scopes = this.Scopes?.ToList(),
            Authority = this.Authority,
        };
    }

    /// <summary>
    /// Builds a <see cref="RemoteAuthentication"/> from a <see cref="PhantomOAuthConnection"/>,
    /// round-tripping the shared fields. When <paramref name="scheme"/> is null the scheme is inferred
    /// from the connection's kind / authentication mode.
    /// </summary>
    public static RemoteAuthentication FromPhantomOAuthConnection(PhantomOAuthConnection connection, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new RemoteAuthentication(
            Scheme: scheme ?? InferScheme(connection),
            Endpoint: connection.Endpoint,
            Authority: connection.Authority,
            ClientId: connection.ClientId,
            ClientSecret: connection.ClientSecret,
            TokenEndpoint: connection.TokenUrl,
            Scopes: connection.Scopes?.ToList());
    }

    private static string InferScheme(PhantomOAuthConnection connection)
        => string.Equals(connection.AuthenticationMode, PhantomAgentSchema.EntraPinnedAuthenticationMode, StringComparison.OrdinalIgnoreCase)
            ? EntraScheme
            : string.Equals(connection.Kind, KeyKind, StringComparison.OrdinalIgnoreCase)
                ? GithubScheme
                : string.Equals(connection.Kind, AnonymousKind, StringComparison.OrdinalIgnoreCase)
                    ? AnonymousScheme
                    : OAuthScheme;
}
