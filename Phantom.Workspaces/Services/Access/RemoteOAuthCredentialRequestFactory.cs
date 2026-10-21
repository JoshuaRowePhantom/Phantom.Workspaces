using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

internal sealed class RemoteOAuthCredentialRequestFactory(OAuthProviderRegistry registry)
{
    internal OAuthCredentialAccessRequest CreateRequest(
        RemoteAuthentication authentication,
        OAuthClientAuthentication clientAuthentication,
        Uri audience,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        var clientIdentifier = clientAuthentication switch
        {
            PublicOAuthClient client => client.Client?.Value,
            ConfidentialOAuthClient client => client.Client.Value,
            _ => null,
        };
        if (!string.Equals(authentication.Scheme?.Trim(), RemoteAuthentication.OAuthScheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(clientIdentifier) || clientIdentifier != authentication.ClientId ||
            (!string.IsNullOrWhiteSpace(authentication.ClientSecret) != (clientAuthentication is ConfidentialOAuthClient)) ||
            (!string.IsNullOrWhiteSpace(authentication.ClientSecret) &&
                !(authentication.ClientSecret.StartsWith("${", StringComparison.Ordinal) &&
                  authentication.ClientSecret.EndsWith("}", StringComparison.Ordinal) && authentication.ClientSecret.Length > 3)) ||
            !TryHttps(authentication.Authority, out var authority) ||
            !TryHttps(authentication.AuthorizationEndpoint, out var authorizationEndpoint) ||
            !TryHttps(authentication.TokenEndpoint, out var tokenEndpoint) ||
            !TryHttps(authentication.AccountEndpoint, out var accountEndpoint) ||
            authentication.AccountIdentifierClaim is not { } claim || !Enum.IsDefined(claim) ||
            audience is null || !audience.IsAbsoluteUri ||
            (authentication.Endpoint is not null &&
                (!Uri.TryCreate(authentication.Endpoint, UriKind.Absolute, out var pinnedAudience) || pinnedAudience != audience)))
        {
            throw new OAuthCredentialConfigurationException();
        }

        return registry.CreateRequest(new OAuthProviderDescriptor
        {
            Authority = authority!,
            AuthorizationEndpoint = authorizationEndpoint!,
            TokenEndpoint = tokenEndpoint!,
            AccountEndpoint = accountEndpoint!,
            AccountIdentifierClaim = claim,
            Audience = audience.AbsoluteUri,
        }, clientAuthentication, (authentication.Scopes ?? []).Select(scope => new OAuthScope { Value = scope }).ToArray(),
            requestor, use, presentationScope);
    }

    private static bool TryHttps(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
}

internal sealed class OAuthCredentialConfigurationException()
    : InvalidOperationException("OAuth credential configuration requires explicit trusted provider and account metadata.")
{
    internal AccessErrorCode ErrorCode => AccessErrorCode.UnsupportedRequest;
}
