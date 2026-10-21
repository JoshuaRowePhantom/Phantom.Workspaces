using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Services.DevTunnel;

internal sealed class DevTunnelIdentityRouteFactory(OAuthProviderRegistry oauthRegistry)
{
    internal DevTunnelIdentityRoute Create(
        RemoteAuthentication? authentication,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        ArgumentNullException.ThrowIfNull(requestor);
        ArgumentNullException.ThrowIfNull(use);
        return (authentication?.Scheme ?? RemoteAuthentication.GithubScheme).Trim().ToLowerInvariant() switch
        {
            RemoteAuthentication.GithubScheme => new GitHubDevTunnelIdentityRoute
            {
                Credential = GitHubCredentialAccessRequest.Create(
                    GitHubCredentialPurpose.DevTunnelIdentity, requestor, use.Lineage, "devtunnel-management",
                    presentationScope),
            },
            RemoteAuthentication.AnonymousScheme => new AnonymousDevTunnelIdentityRoute(),
            RemoteAuthentication.EntraScheme => CreateEntra(authentication!, requestor, use, presentationScope),
            RemoteAuthentication.OAuthScheme => this.CreateOAuth(authentication!, requestor, use, presentationScope),
            _ => throw Unsupported(),
        };
    }

    private static EntraDevTunnelIdentityRoute CreateEntra(
        RemoteAuthentication authentication,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        var authority = RequireEndpoint(authentication.Authority);
        var client = RequireText(authentication.ClientId);
        if (!string.IsNullOrWhiteSpace(authentication.ClientSecret))
        {
            throw Unsupported();
        }
        var partition = string.IsNullOrWhiteSpace(authentication.TokenCacheKey)
            ? "devtunnel-management" : authentication.TokenCacheKey.Trim();
        var scopes = CanonicalScopes(authentication.Scopes);
        if (scopes.Length == 0)
        {
            scopes = ["https://tunnels.api.visualstudio.com/.default"];
        }
        var logical = CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
        {
            Provider = CredentialProviderKind.Entra,
            Kind = CredentialKind.AccessToken,
            CredentialPartition = partition,
            SourceCompatibility = "entra-sdk-cache-v1",
            Authority = authority.AbsoluteUri,
            Audience = string.Empty,
            ClientPartition = client,
            Scopes = scopes,
        });
        return new EntraDevTunnelIdentityRoute
        {
            Credential = new EntraCredentialAccessRequest
            {
                Authority = new EntraAuthority { Value = authority },
                Client = new EntraClientIdentifier { Value = client },
                CachePartition = new EntraCachePartition { Value = partition },
                Scopes = scopes.Select(scope => new EntraScope { Value = scope }).ToArray(),
                Credential = new CredentialReference
                {
                    Provider = CredentialProviderKind.Entra,
                    Kind = CredentialKind.AccessToken,
                    LogicalIdentifier = logical,
                },
                Sources = [new CredentialSourceChoiceDescriptor
                {
                    Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.Entra, logical.Digest),
                    Provider = CredentialProviderKind.Entra,
                    Kind = CredentialSourceKind.ProviderCache,
                    Usability = CredentialSourceUsability.AcquisitionRequired,
                    SafeDisplayName = "Microsoft account",
                }],
                Requestor = requestor,
                Use = use,
                PresentationScope = presentationScope,
            },
        };
    }

    private OAuthDevTunnelIdentityRoute CreateOAuth(
        RemoteAuthentication authentication,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        var provider = new OAuthProviderDescriptor
        {
            Authority = RequireEndpoint(authentication.Authority),
            AuthorizationEndpoint = RequireEndpoint(authentication.AuthorizationEndpoint),
            TokenEndpoint = RequireEndpoint(authentication.TokenEndpoint),
            AccountEndpoint = RequireEndpoint(authentication.AccountEndpoint),
            Audience = RequireText(authentication.Audience),
            AccountIdentifierClaim = authentication.AccountIdentifierClaim is { } claim && Enum.IsDefined(claim)
                ? claim : throw Unsupported(),
        };
        var clientId = new OAuthClientIdentifier { Value = RequireText(authentication.ClientId) };
        OAuthClientAuthentication client = new PublicOAuthClient { Client = clientId };
        if (!string.IsNullOrWhiteSpace(authentication.ClientSecret))
        {
            var placeholder = authentication.ClientSecret.Trim();
            const string prefix = "${SECRET:";
            if (!placeholder.StartsWith(prefix, StringComparison.Ordinal) ||
                !placeholder.EndsWith('}') || placeholder.Length <= prefix.Length + 1)
            {
                throw Unsupported();
            }
            var name = placeholder[prefix.Length..^1];
            if (string.IsNullOrWhiteSpace(name) || name.Any(character =>
                    char.IsControl(character) || character is '{' or '}'))
            {
                throw Unsupported();
            }
            client = new ConfidentialOAuthClient
            {
                Client = clientId,
                ClientSecret = new SecretReference { Name = name },
            };
        }
        return new OAuthDevTunnelIdentityRoute
        {
            Credential = oauthRegistry.CreateRequest(provider, client,
                CanonicalScopes(authentication.Scopes).Select(scope => new OAuthScope { Value = scope }).ToArray(),
                requestor, use, presentationScope),
        };
    }

    private static string[] CanonicalScopes(IReadOnlyList<string>? scopes) =>
        (scopes ?? []).Where(scope => !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string RequireText(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? throw Unsupported() : value.Trim();

    private static Uri RequireEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
            ? uri : throw Unsupported();

    private static DevTunnelCredentialAccessException Unsupported() => new(AccessErrorCode.UnsupportedRequest);
}
