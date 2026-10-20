using System.Net.Http;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Access;

internal sealed record OAuthProviderRegistration
{
    public required OAuthProviderDescriptor Provider { get; init; }
    public required OAuthClientAuthentication ClientAuthentication { get; init; }
    public required IReadOnlyList<OAuthScope> Scopes { get; init; }
    public required CredentialReference Credential { get; init; }
    public required CredentialSourceIdentifier Source { get; init; }
}

internal sealed class OAuthProviderRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<LogicalCredentialIdentifier, OAuthProviderConfiguration> configurations = [];
    private readonly IPlatformSecretStore store;
    private readonly Func<Uri> createRedirectUri;
    private readonly Func<HttpMessageHandler> createHttpHandler;
    private readonly TimeProvider timeProvider;

    internal OAuthProviderRegistry(
        IPlatformSecretStore store,
        Func<Uri> createRedirectUri,
        Func<HttpMessageHandler>? createHttpHandler = null,
        TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.createRedirectUri = createRedirectUri;
        this.createHttpHandler = createHttpHandler ?? (() => new SocketsHttpHandler { AllowAutoRedirect = false });
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal OAuthProviderRegistration RegisterProvider(
        OAuthProviderDescriptor provider,
        OAuthClientAuthentication clientAuthentication,
        IReadOnlyList<OAuthScope> scopes)
    {
        var canonicalScopes = Array.AsReadOnly(scopes.Select(scope => scope.Value).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(value => new OAuthScope { Value = value }).ToArray());
        var credential = OAuthCredentialAccessExecutor.DescribeCredential(provider, clientAuthentication, canonicalScopes);
        lock (this.gate)
        {
            if (!this.configurations.TryGetValue(credential.LogicalIdentifier, out var configuration))
            {
                configuration = new OAuthProviderConfiguration
                {
                    Provider = provider,
                    ClientAuthentication = clientAuthentication,
                    Scopes = canonicalScopes,
                    Cache = new CredentialManagerTokenCache(this.store, "oauth:", credential.LogicalIdentifier.Digest),
                    CreateRedirectUri = this.createRedirectUri,
                    CreateHttpHandler = this.createHttpHandler,
                    TimeProvider = this.timeProvider,
                };
                this.configurations.Add(credential.LogicalIdentifier, configuration);
            }
            return new OAuthProviderRegistration
            {
                Provider = configuration.Provider,
                ClientAuthentication = configuration.ClientAuthentication,
                Scopes = configuration.Scopes,
                Credential = credential,
                Source = configuration.Source,
            };
        }
    }

    internal OAuthCredentialAccessRequest CreateRequest(
        OAuthProviderDescriptor provider,
        OAuthClientAuthentication clientAuthentication,
        IReadOnlyList<OAuthScope> scopes,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope)
    {
        var registration = this.RegisterProvider(provider, clientAuthentication, scopes);
        return new OAuthCredentialAccessRequest
        {
            Provider = registration.Provider,
            ClientAuthentication = registration.ClientAuthentication,
            Scopes = registration.Scopes,
            Credential = registration.Credential,
            Sources = [new CredentialSourceChoiceDescriptor
            {
                Identifier = registration.Source,
                Provider = CredentialProviderKind.OAuth,
                Kind = CredentialSourceKind.Provider,
                Usability = CredentialSourceUsability.AcquisitionRequired,
                SafeDisplayName = "OAuth provider",
            }],
            Requestor = requestor,
            Use = use,
            PresentationScope = presentationScope,
        };
    }

    internal OAuthProviderConfiguration? FindConfiguration(LogicalCredentialIdentifier identifier)
    {
        lock (this.gate) { return this.configurations.GetValueOrDefault(identifier); }
    }

    internal IReadOnlyList<OAuthProviderConfiguration> SnapshotConfigurations()
    {
        lock (this.gate) { return this.configurations.Values.ToArray(); }
    }
}
