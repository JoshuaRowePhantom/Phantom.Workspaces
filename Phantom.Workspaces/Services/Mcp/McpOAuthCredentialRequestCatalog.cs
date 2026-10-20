using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class McpOAuthCredentialRequestCatalog(
    IReadOnlyList<McpOAuthProviderConfiguration> configurations) : IMcpOAuthCredentialRequestCatalog
{
    public McpOAuthCredentialUseRequest? CreateRequest(
        McpTool tool,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        if (tool.Connection is not OAuthConnection connection ||
            (!string.IsNullOrEmpty(connection.AuthenticationMode) &&
                !string.Equals(connection.AuthenticationMode, "system", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(connection.TokenUrl) ||
            !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        var configuration = configurations.SingleOrDefault(candidate =>
            candidate.Endpoint == endpoint && MatchesAuthority(candidate.AuthorizationServer, connection) &&
            MatchesClient(candidate.ClientAuthentication, connection) &&
            candidate.Scopes.Select(scope => scope.Value).Order(StringComparer.Ordinal)
                .SequenceEqual((connection.Scopes ?? []).Order(StringComparer.Ordinal)));
        if (configuration is null)
        {
            return null;
        }

        var credential = McpOAuthCredentialAccessExecutor.DescribeCredential(configuration);
        var lineage = use.Lineage with
        {
            ProviderConfiguration = CredentialIdentityFactory.CreateLineageIdentifier(
                "mcp-provider", configuration.Identifier.Digest),
        };
        return new McpOAuthCredentialUseRequest
        {
            ProviderConfiguration = configuration.Identifier,
            ClientAuthentication = configuration.ClientAuthentication,
            Scopes = configuration.Scopes,
            Credential = credential,
            Requestor = requestor,
            Use = use with
            {
                Kind = CredentialUseKind.Mcp,
                Lineage = lineage,
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                    CredentialUseKind.Mcp, lineage, requestor.Identifier,
                    $"mcp:{use.Fingerprint.Digest}:{credential.LogicalIdentifier.Digest}"),
            },
            PresentationScope = presentationScope,
            Sources = [new()
            {
                Identifier = configuration.Source,
                Provider = CredentialProviderKind.McpOAuth,
                Kind = CredentialSourceKind.Provider,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = "OAuth provider",
            }],
        };
    }

    private static bool MatchesClient(
        OAuthClientAuthentication authentication,
        OAuthConnection connection) => authentication switch
        {
            PublicOAuthClient client => string.IsNullOrEmpty(connection.ClientSecret) &&
                string.Equals(client.Client?.Value ?? "", connection.ClientId ?? "", StringComparison.Ordinal),
            ConfidentialOAuthClient client => connection.ClientId == client.Client.Value &&
                connection.ClientSecret == $"${{SECRET:{client.ClientSecret.Name}}}",
            _ => false,
        };

    private static bool MatchesAuthority(
        Uri authority,
        OAuthConnection connection) => connection is not PhantomOAuthConnection configured ||
            string.IsNullOrEmpty(configured.Authority) ||
            (Uri.TryCreate(configured.Authority, UriKind.Absolute, out var expected) && authority == expected);
}
