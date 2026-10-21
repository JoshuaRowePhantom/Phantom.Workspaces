using AgentSchema;
using ModelContextProtocol.Client;
using System.Net.Http;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class McpOAuthCredentialRequestCatalog : IMcpOAuthCredentialRequestCatalog
{
    private readonly object gate = new();
    private readonly Dictionary<McpProviderConfigurationIdentifier, McpOAuthProviderConfiguration> configurations = [];
    private readonly Func<McpTool, McpOAuthProviderConfiguration?>? configurationFactory;

    internal McpOAuthCredentialRequestCatalog(IEnumerable<McpOAuthProviderConfiguration>? configurations = null)
    {
        foreach (var configuration in configurations ?? [])
        {
            this.RegisterConfiguration(configuration);
        }
    }

    internal McpOAuthCredentialRequestCatalog(Func<McpTool, McpOAuthProviderConfiguration?> configurationFactory)
    {
        this.configurationFactory = configurationFactory;
    }

    internal McpOAuthCredentialRequestCatalog(
        IPlatformSecretStore platformSecretStore,
        Func<HttpMessageHandler>? createHttpHandler = null)
    {
        this.configurationFactory = new McpOAuthProviderConfigurationFactory(platformSecretStore, createHttpHandler).Create;
    }

    internal void RegisterConfiguration(McpOAuthProviderConfiguration configuration)
    {
        var owned = configuration with { Scopes = configuration.Scopes.ToArray() };
        lock (this.gate)
        {
            if (this.configurations.TryGetValue(owned.Identifier, out var current))
            {
                if (McpOAuthCredentialAccessExecutor.DescribeCredential(current).LogicalIdentifier !=
                    McpOAuthCredentialAccessExecutor.DescribeCredential(owned).LogicalIdentifier ||
                    current.BindToCallerDeclarations != owned.BindToCallerDeclarations ||
                    !current.Cache.SharesPartitionWith(owned.Cache))
                {
                    throw new InvalidOperationException("MCP provider configuration is already registered with different bindings.");
                }
                return;
            }
            this.configurations.Add(owned.Identifier, owned);
        }
    }

    internal McpOAuthProviderConfiguration? FindConfiguration(McpProviderConfigurationIdentifier identifier)
    {
        lock (this.gate) { return this.configurations.GetValueOrDefault(identifier); }
    }

    internal IReadOnlyList<McpOAuthProviderConfiguration> GetConfigurations()
    {
        lock (this.gate)
        {
            return this.configurations.Values.Select(configuration => configuration with
            {
                Scopes = configuration.Scopes.ToArray(),
            }).ToArray();
        }
    }

    public McpOAuthCredentialUseRequest? CreateRequest(
        McpTool tool,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        if (tool.Connection is not OAuthConnection connection ||
            (!string.IsNullOrEmpty(connection.AuthenticationMode) &&
                !string.Equals(connection.AuthenticationMode, "system", StringComparison.OrdinalIgnoreCase)) ||
            !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        var mode = tool is PhantomMcpTool typed ? typed.Transport : McpHttpTransport.Streamable;
        var transport = mode switch
        {
            McpHttpTransport.Sse => HttpTransportMode.Sse,
            McpHttpTransport.Auto => HttpTransportMode.AutoDetect,
            _ => HttpTransportMode.StreamableHttp,
        };
        bool Matches(McpOAuthProviderConfiguration candidate) =>
            candidate.Endpoint == endpoint && MatchesAuthority(candidate.AuthorizationServer, connection, candidate.BindToCallerDeclarations) &&
            MatchesTokenEndpoint(candidate.ExpectedTokenEndpoint, connection.TokenUrl, candidate.BindToCallerDeclarations) &&
            candidate.TransportMode == transport && MatchesClient(candidate.ClientAuthentication, connection) &&
            candidate.Scopes.Select(scope => scope.Value).Order(StringComparer.Ordinal)
                .SequenceEqual((connection.Scopes ?? []).Order(StringComparer.Ordinal));
        var matches = this.GetConfigurations().Where(Matches).Take(2).ToArray();
        if (matches.Length > 1) { return null; }
        var configuration = matches.SingleOrDefault();
        if (configuration is null && this.configurationFactory is { } createConfiguration)
        {
            var created = createConfiguration(tool);
            if (created is not null && Matches(created))
            {
                this.RegisterConfiguration(created);
                configuration = this.FindConfiguration(created.Identifier);
            }
        }
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
            Scopes = configuration.Scopes.ToArray(),
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
        Uri? authority,
        OAuthConnection connection,
        bool bindToCallerDeclarations)
    {
        var declared = (connection as PhantomOAuthConnection)?.Authority;
        // Trusted advanced adapters may supply omitted pins; canonical first-use discovery is a distinct policy.
        return string.IsNullOrEmpty(declared)
            ? !bindToCallerDeclarations || authority is null
            : Uri.TryCreate(declared, UriKind.Absolute, out var expected) && authority == expected;
    }

    private static bool MatchesTokenEndpoint(Uri? expected, string? declared, bool bindToCallerDeclarations) =>
        string.IsNullOrEmpty(declared)
            ? !bindToCallerDeclarations || expected is null
            : Uri.TryCreate(declared, UriKind.Absolute, out var endpoint) && expected == endpoint;
}
