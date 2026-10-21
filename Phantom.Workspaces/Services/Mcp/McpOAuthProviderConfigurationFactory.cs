using AgentSchema;
using System.Net.Http;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class McpOAuthProviderConfigurationFactory(
    IPlatformSecretStore store,
    Func<HttpMessageHandler>? createHttpHandler = null)
{
    internal McpOAuthProviderConfiguration? Create(McpTool tool)
    {
        if (store is NullPlatformSecretStore || tool.Connection is not OAuthConnection connection ||
            (!string.IsNullOrEmpty(connection.AuthenticationMode) &&
                !string.Equals(connection.AuthenticationMode, "system", StringComparison.OrdinalIgnoreCase)) ||
            !TrySecureUri(connection.Endpoint, out var endpoint) ||
            (connection.ClientId?.Contains("${", StringComparison.Ordinal) ?? false))
        {
            return null;
        }
        Uri? authority = null;
        if (connection is PhantomOAuthConnection { Authority: { Length: > 0 } configured } &&
            !TrySecureUri(configured, out authority))
        {
            return null;
        }
        Uri? tokenEndpoint = null;
        if (!string.IsNullOrEmpty(connection.TokenUrl) && !TrySecureUri(connection.TokenUrl, out tokenEndpoint))
        {
            return null;
        }

        OAuthClientAuthentication authentication;
        if (string.IsNullOrEmpty(connection.ClientSecret))
        {
            authentication = new PublicOAuthClient
            {
                Client = string.IsNullOrEmpty(connection.ClientId) ? null : new() { Value = connection.ClientId },
            };
        }
        else
        {
            const string prefix = "${SECRET:";
            if (string.IsNullOrEmpty(connection.ClientId) || !connection.ClientSecret.StartsWith(prefix, StringComparison.Ordinal) ||
                !connection.ClientSecret.EndsWith('}') || connection.ClientSecret.Length <= prefix.Length + 1)
            {
                return null;
            }
            var name = connection.ClientSecret[prefix.Length..^1];
            // Per-materialization handles are not stable provider configuration or store names.
            if (Guid.TryParseExact(name, "N", out _))
            {
                return null;
            }
            authentication = new ConfidentialOAuthClient
            {
                Client = new() { Value = connection.ClientId },
                ClientSecret = new() { Name = name },
            };
        }
        var mode = tool is PhantomMcpTool typed ? typed.Transport : McpHttpTransport.Streamable;
        var transportMode = mode switch
        {
            McpHttpTransport.Sse => HttpTransportMode.Sse,
            McpHttpTransport.Auto => HttpTransportMode.AutoDetect,
            _ => HttpTransportMode.StreamableHttp,
        };
        var scopes = (connection.Scopes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(scope => new OAuthScope { Value = scope }).ToArray();
        var clientPartition = authentication switch
        {
            ConfidentialOAuthClient client => $"confidential:{client.Client.Value}:{client.ClientSecret.Name}",
            PublicOAuthClient client => $"public:{client.Client?.Value}",
            _ => throw new InvalidOperationException("Unsupported OAuth client authentication."),
        };
        var canonical = CredentialIdentityFactory.CreateLogicalIdentifier(new()
        {
            Provider = CredentialProviderKind.McpOAuth,
            Kind = CredentialKind.OpaqueUse,
            CredentialPartition = "per-tool-v1",
            SourceCompatibility = $"rfc9728:{transportMode}:{tokenEndpoint?.AbsoluteUri}",
            Authority = authority?.AbsoluteUri ?? "resource-delegated-discovery-v1",
            Audience = endpoint.AbsoluteUri,
            ClientPartition = clientPartition,
            Scopes = scopes.Select(scope => scope.Value).ToArray(),
        });
        var identifier = new McpProviderConfigurationIdentifier { Digest = canonical.Digest };
        var configuration = new McpOAuthProviderConfiguration
        {
            Identifier = identifier,
            Endpoint = endpoint,
            AuthorizationServer = authority,
            ExpectedTokenEndpoint = tokenEndpoint,
            BindToCallerDeclarations = true,
            ClientAuthentication = authentication,
            Scopes = scopes,
            TransportMode = transportMode,
            Source = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.McpOAuth, $"per-tool:{identifier.Digest}"),
            Cache = new CredentialManagerTokenCache(store, "mcp-oauth:", identifier.Digest),
        };
        return createHttpHandler is null ? configuration : configuration with { CreateHttpHandler = createHttpHandler };
    }

    private static bool TrySecureUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
}
