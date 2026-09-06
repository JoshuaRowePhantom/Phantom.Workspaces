using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>The <c>github</c> auth scheme: token via environment / <c>gh</c>; no fields.</summary>
public sealed class GitHubAuthSchemeViewModel : AuthSchemeViewModel
{
    /// <inheritdoc />
    public override string Scheme => RemoteAuthentication.GithubScheme;

    /// <inheritdoc />
    public override string Description =>
        "Authorizes with your GitHub auth token (GITHUB_TOKEN environment variable, or 'gh auth token'). No fields required.";

    /// <inheritdoc />
    public override string? ValidationMessage => null;

    /// <inheritdoc />
    public override RemoteAuthentication ToRemoteAuthentication() => new(RemoteAuthentication.GithubScheme);
}

/// <summary>The <c>anonymous</c> auth scheme: no credential attached; no fields.</summary>
public sealed class AnonymousAuthSchemeViewModel : AuthSchemeViewModel
{
    /// <inheritdoc />
    public override string Scheme => RemoteAuthentication.AnonymousScheme;

    /// <inheritdoc />
    public override string Description =>
        "No credential is attached. The remote must permit anonymous access.";

    /// <inheritdoc />
    public override string? ValidationMessage => null;

    /// <inheritdoc />
    public override RemoteAuthentication ToRemoteAuthentication() => new(RemoteAuthentication.AnonymousScheme);
}

/// <summary>
/// The <c>entra</c> auth scheme: host-pinned Microsoft Entra auth. Requires a client id and a valid
/// absolute authority URI; tenant id and scopes are optional.
/// </summary>
public sealed class EntraAuthSchemeViewModel : AuthSchemeViewModel
{
    private string? clientId;
    private string? authority;
    private string? tenantId;
    private string? scopes;

    /// <summary>Creates an empty view model.</summary>
    public EntraAuthSchemeViewModel()
    {
    }

    /// <summary>Creates a view model initialized from an existing <see cref="RemoteAuthentication"/>.</summary>
    public EntraAuthSchemeViewModel(RemoteAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        this.clientId = authentication.ClientId;
        this.authority = authentication.Authority;
        this.tenantId = authentication.TenantId;
        this.scopes = AuthSchemeScopes.Join(authentication.Scopes);
    }

    /// <inheritdoc />
    public override string Scheme => RemoteAuthentication.EntraScheme;

    /// <summary>Entra application (client) id. Required.</summary>
    public string? ClientId
    {
        get => this.clientId;
        set => this.SetValidatedProperty(ref this.clientId, value);
    }

    /// <summary>Entra authority URL (e.g. <c>https://login.microsoftonline.com/&lt;tenant&gt;</c>). Required.</summary>
    public string? Authority
    {
        get => this.authority;
        set => this.SetValidatedProperty(ref this.authority, value);
    }

    /// <summary>Optional Entra tenant id.</summary>
    public string? TenantId
    {
        get => this.tenantId;
        set => this.SetValidatedProperty(ref this.tenantId, value);
    }

    /// <summary>Optional space/newline-separated scopes.</summary>
    public string? Scopes
    {
        get => this.scopes;
        set => this.SetValidatedProperty(ref this.scopes, value);
    }

    /// <inheritdoc />
    public override string Description =>
        "Sign in with Microsoft Entra ID. Requires a client (application) id and an authority URL.";

    /// <inheritdoc />
    public override string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(this.ClientId))
            {
                return "Entra requires a ClientId.";
            }

            if (!Uri.TryCreate(this.Authority, UriKind.Absolute, out _))
            {
                return "Entra requires a valid absolute Authority URI.";
            }

            return null;
        }
    }

    /// <inheritdoc />
    public override RemoteAuthentication ToRemoteAuthentication() => new(
        Scheme: RemoteAuthentication.EntraScheme,
        Authority: AuthSchemeScopes.NullIfBlank(this.Authority),
        TenantId: AuthSchemeScopes.NullIfBlank(this.TenantId),
        ClientId: AuthSchemeScopes.NullIfBlank(this.ClientId),
        Scopes: AuthSchemeScopes.Parse(this.Scopes));
}

/// <summary>
/// The <c>oauth</c> auth scheme: standard interactive OAuth. Requires authorization/token endpoints and
/// a client id; the secret is only ever a <c>${SECRET:…}</c> placeholder, never a raw value.
/// </summary>
public sealed class OAuthAuthSchemeViewModel : AuthSchemeViewModel
{
    private string? authorizationEndpoint;
    private string? tokenEndpoint;
    private string? clientId;
    private string? clientSecret;
    private string? scopes;

    /// <summary>Creates an empty view model.</summary>
    public OAuthAuthSchemeViewModel()
    {
    }

    /// <summary>Creates a view model initialized from an existing <see cref="RemoteAuthentication"/>.</summary>
    public OAuthAuthSchemeViewModel(RemoteAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        this.authorizationEndpoint = authentication.AuthorizationEndpoint;
        this.tokenEndpoint = authentication.TokenEndpoint;
        this.clientId = authentication.ClientId;
        this.clientSecret = authentication.ClientSecret;
        this.scopes = AuthSchemeScopes.Join(authentication.Scopes);
    }

    /// <inheritdoc />
    public override string Scheme => RemoteAuthentication.OAuthScheme;

    /// <summary>OAuth authorization endpoint URL. Required.</summary>
    public string? AuthorizationEndpoint
    {
        get => this.authorizationEndpoint;
        set => this.SetValidatedProperty(ref this.authorizationEndpoint, value);
    }

    /// <summary>OAuth token endpoint URL. Required.</summary>
    public string? TokenEndpoint
    {
        get => this.tokenEndpoint;
        set => this.SetValidatedProperty(ref this.tokenEndpoint, value);
    }

    /// <summary>OAuth client id. Required.</summary>
    public string? ClientId
    {
        get => this.clientId;
        set => this.SetValidatedProperty(ref this.clientId, value);
    }

    /// <summary>Optional client-secret placeholder (<c>${SECRET:…}</c> / <c>${ENV}</c>), never raw.</summary>
    public string? ClientSecret
    {
        get => this.clientSecret;
        set => this.SetValidatedProperty(ref this.clientSecret, value);
    }

    /// <summary>Optional space/newline-separated scopes.</summary>
    public string? Scopes
    {
        get => this.scopes;
        set => this.SetValidatedProperty(ref this.scopes, value);
    }

    /// <inheritdoc />
    public override string Description =>
        "Standard interactive OAuth. Requires authorization/token endpoints and a client id. Provide the secret as a ${SECRET:...} placeholder, never a raw value.";

    /// <inheritdoc />
    public override string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(this.ClientId))
            {
                return "OAuth requires a ClientId.";
            }

            if (!Uri.TryCreate(this.AuthorizationEndpoint, UriKind.Absolute, out _))
            {
                return "OAuth requires a valid AuthorizationEndpoint URL.";
            }

            if (!Uri.TryCreate(this.TokenEndpoint, UriKind.Absolute, out _))
            {
                return "OAuth requires a valid TokenEndpoint URL.";
            }

            return null;
        }
    }

    /// <inheritdoc />
    public override RemoteAuthentication ToRemoteAuthentication() => new(
        Scheme: RemoteAuthentication.OAuthScheme,
        ClientId: AuthSchemeScopes.NullIfBlank(this.ClientId),
        ClientSecret: AuthSchemeScopes.NullIfBlank(this.ClientSecret),
        AuthorizationEndpoint: AuthSchemeScopes.NullIfBlank(this.AuthorizationEndpoint),
        TokenEndpoint: AuthSchemeScopes.NullIfBlank(this.TokenEndpoint),
        Scopes: AuthSchemeScopes.Parse(this.Scopes));
}

/// <summary>Helpers for parsing/joining the free-text scopes field shared by the OAuth-style schemes.</summary>
internal static class AuthSchemeScopes
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ','];

    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static IReadOnlyList<string>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var scopes = value
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return scopes.Count == 0 ? null : scopes;
    }

    public static string? Join(IReadOnlyList<string>? scopes)
        => scopes is { Count: > 0 } ? string.Join(" ", scopes) : null;
}
