using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;

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
    private readonly RemoteAuthentication original = new(RemoteAuthentication.OAuthScheme);
    private string? authority;
    private string? accountEndpoint;
    private OAuthAccountIdentifierClaim? accountIdentifierClaim;
    private string? audience;
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
        this.original = authentication;
        this.authority = authentication.Authority;
        this.accountEndpoint = authentication.AccountEndpoint;
        this.accountIdentifierClaim = authentication.AccountIdentifierClaim;
        this.audience = authentication.Audience;
        this.authorizationEndpoint = authentication.AuthorizationEndpoint;
        this.tokenEndpoint = authentication.TokenEndpoint;
        this.clientId = authentication.ClientId;
        this.clientSecret = authentication.ClientSecret;
        this.scopes = AuthSchemeScopes.Join(authentication.Scopes);
    }

    /// <inheritdoc />
    public override string Scheme => RemoteAuthentication.OAuthScheme;

    public IReadOnlyList<OAuthAccountIdentifierClaim> AccountIdentifierClaims { get; } =
        [OAuthAccountIdentifierClaim.Subject, OAuthAccountIdentifierClaim.Identifier];

    public string? Authority
    {
        get => this.authority;
        set => this.SetValidatedProperty(ref this.authority, value);
    }

    public string? AccountEndpoint
    {
        get => this.accountEndpoint;
        set => this.SetValidatedProperty(ref this.accountEndpoint, value);
    }

    public OAuthAccountIdentifierClaim? AccountIdentifierClaim
    {
        get => this.accountIdentifierClaim;
        set => this.SetValidatedProperty(ref this.accountIdentifierClaim, value);
    }

    public string? Audience
    {
        get => this.audience;
        set => this.SetValidatedProperty(ref this.audience, value);
    }

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
        "Interactive OAuth requires explicit trusted HTTPS authority, authorization, token and account endpoints, an account claim (Subject = sub; Identifier = id), an audience and a client id. No endpoints or account identities are inferred. Provide any secret as a ${SECRET:...} placeholder, never a raw value.";

    /// <inheritdoc />
    public override string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(this.ClientId))
            {
                return "OAuth requires a ClientId.";
            }

            if (!IsTrustedEndpoint(this.Authority))
            {
                return "OAuth requires a trusted HTTPS Authority URL without credentials or a fragment.";
            }

            if (!IsTrustedEndpoint(this.AuthorizationEndpoint))
            {
                return "OAuth requires a trusted HTTPS AuthorizationEndpoint URL without credentials or a fragment.";
            }

            if (!IsTrustedEndpoint(this.TokenEndpoint))
            {
                return "OAuth requires a trusted HTTPS TokenEndpoint URL without credentials or a fragment.";
            }

            if (!IsTrustedEndpoint(this.AccountEndpoint))
            {
                return "OAuth requires an explicit trusted HTTPS AccountEndpoint URL without credentials or a fragment.";
            }

            if (this.AccountIdentifierClaim is not { } claim || !Enum.IsDefined(claim))
            {
                return "OAuth requires an explicit account claim: Subject (sub) or Identifier (id).";
            }

            if (string.IsNullOrWhiteSpace(this.Audience))
            {
                return "OAuth requires an explicit Audience.";
            }

            return null;
        }
    }

    /// <inheritdoc />
    public override RemoteAuthentication ToRemoteAuthentication() => this.original with
    {
        Scheme = RemoteAuthentication.OAuthScheme,
        Authority = AuthSchemeScopes.NullIfBlank(this.Authority),
        ClientId = AuthSchemeScopes.NullIfBlank(this.ClientId),
        ClientSecret = AuthSchemeScopes.NullIfBlank(this.ClientSecret),
        AuthorizationEndpoint = AuthSchemeScopes.NullIfBlank(this.AuthorizationEndpoint),
        TokenEndpoint = AuthSchemeScopes.NullIfBlank(this.TokenEndpoint),
        Scopes = AuthSchemeScopes.Parse(this.Scopes),
        AccountEndpoint = AuthSchemeScopes.NullIfBlank(this.AccountEndpoint),
        AccountIdentifierClaim = this.AccountIdentifierClaim,
        Audience = AuthSchemeScopes.NullIfBlank(this.Audience),
    };

    private static bool IsTrustedEndpoint(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
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
